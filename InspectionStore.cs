// =====================================================================
// InspectionStore.cs  -  SQLite 검사 이력 저장소
// =====================================================================
// 역할: 제품별 검사 이력(최초 판정 + 재검사)을 data/qualitymate.db 에 저장하고 읽는다.
//       Program.cs의 엔드포인트가 이 클래스를 통해서만 DB에 접근한다.
// 
// 데이터 흐름
//   GET  /api/inspections          -> All()     전체 조회
//   POST .../reinspections          -> Append()  재검사 1건 추가 (멱등성 + 버전 충돌 검사)
//   POST /api/import/visionqc       -> Import()  CSV로 만든 검사 목록 일괄 추가 (원자적)
// 
// 설계 포인트
//   - 최초 판정(Inspections)과 재검사(Reinspections)를 다른 테이블에 분리한다.
//     재검사는 INSERT만 하고 UPDATE/DELETE 하지 않으므로 원본 판정과 이력이 보존된다.
//   - 모든 쓰기는 트랜잭션 안에서 "읽기 -> 판단 -> 쓰기"를 수행해 중간 상태가 남지 않게 한다.
//   - 연결은 요청마다 새로 열고 닫는다(Open). 서비스는 싱글톤이지만 연결 객체는 공유하지 않는다.
// =====================================================================
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

/// <summary>SQLite 기반 검사 이력 저장소. 앱 전체에서 하나의 인스턴스를 공유(싱글톤)하며, 연결 문자열만 보관하고 연결은 호출마다 새로 연다.</summary>
sealed class InspectionStore
{
    // SQLite 연결 문자열(DB 파일 경로, 외래 키 사용, 10초 대기 설정 포함).
    private readonly string connectionString;
    /// <summary>
    /// 저장소를 초기화한다: 데이터 폴더 생성 -> 테이블 생성(없을 때만) -> 비어 있으면 초기 데이터 입력.
    /// </summary>
    /// <param name="environment">앱 실행 폴더(ContentRootPath) 정보. DataDirectory 설정이 없을 때 기본 data 폴더 위치를 정하는 데 쓴다.</param>
    /// <param name="configuration">DataDirectory 설정 값. 테스트 스크립트가 --DataDirectory로 별도 DB 폴더를 지정해 실제 데이터와 분리한다.</param>
    public InspectionStore(IWebHostEnvironment environment, IConfiguration configuration)
    {
        // DB 폴더: 설정이 있으면 그 경로, 없으면 앱 폴더 아래 data. 시험은 별도 폴더를 지정해 시연 데이터를 건드리지 않는다.
        var directory = configuration["DataDirectory"] ?? Path.Combine(environment.ContentRootPath, "data");
        Directory.CreateDirectory(directory);
        // ForeignKeys = true: SQLite는 기본적으로 외래 키를 검사하지 않으므로 반드시 켜야 REFERENCES가 동작한다.
        // DefaultTimeout = 10: 다른 연결이 쓰는 중이면 최대 10초까지 기다린 뒤 실패한다.
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "qualitymate.db"), ForeignKeys = true, DefaultTimeout = 10
        }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        // 스키마 생성(IF NOT EXISTS라 앱을 다시 시작해도 안전). 아래 SQL 문자열 안에는 주석을 넣지 않고 여기에 설명한다.
        //  - PRAGMA journal_mode=WAL: 쓰기 로그(WAL) 방식. 읽기와 쓰기가 서로 덜 막고, 비정상 종료에도 강하다.
        //  - Inspections: 제품 1개당 1행. Id는 기본 키(중복 불가). InitialResult는 CHECK로 PASS/FAIL/ERROR만 허용한다.
        //    (ERROR는 VisionQC가 검사 자체에 실패한 경우이며 불합격과 구분해 보존한다.)
        //  - Reinspections: 재검사 1건당 1행. Sequence(AUTOINCREMENT)가 입력 순서를 보장한다.
        //    ProductId는 외래 키(REFERENCES)라 없는 제품에 재검사를 달 수 없다.
        //    RequestId는 UNIQUE: 같은 요청 ID가 두 번 저장되는 것을 DB 수준에서 막는 멱등성의 최종 안전장치다.
        //    Result는 CHECK로 PASS/FAIL만 허용한다(재검사 결과는 ERROR 불가).
        //  - IX_Reinspections_Product: 제품별 재검사를 순서대로 읽는 조회를 빠르게 하는 인덱스.
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS Inspections(
                Id TEXT PRIMARY KEY, Lot TEXT NOT NULL,
                InitialResult TEXT NOT NULL CHECK(InitialResult IN ('PASS','FAIL','ERROR')),
                Defect TEXT NOT NULL, InspectedAt TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Reinspections(
                Sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                ProductId TEXT NOT NULL REFERENCES Inspections(Id),
                RequestId TEXT NOT NULL UNIQUE,
                Result TEXT NOT NULL CHECK(Result IN ('PASS','FAIL')),
                Operator TEXT NOT NULL, Note TEXT NOT NULL, CreatedAt TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_Reinspections_Product ON Reinspections(ProductId,Sequence);
            """;
        command.ExecuteNonQuery();
        // 이미 데이터가 있으면 초기 입력을 건너뛴다(재시작 시 중복 입력 방지).
        command.CommandText = "SELECT COUNT(*) FROM Inspections";
        if (Convert.ToInt32(command.ExecuteScalar()) > 0) return;
        // 초기 데이터 결정: 예전 버전이 남긴 inspections.json이 있으면 그것을 읽어 DB로 이관하고,
        // 없으면 시연용 제품 3개(QM-001~003)를 넣는다. JSON 원본은 지우지 않고 그대로 보존한다.
        var legacy = Path.Combine(directory, "inspections.json");
        var seed = File.Exists(legacy)
            ? JsonSerializer.Deserialize<List<Inspection>>(File.ReadAllText(legacy)) ?? throw new InvalidDataException("기존 데이터 읽기 실패")
            : new List<Inspection>
            {
                new("QM-001", "LOT-DEMO-A", "FAIL", "표면 흠집 의심", DateTimeOffset.Parse("2026-11-19T09:20:00+09:00"), []),
                new("QM-002", "LOT-DEMO-A", "PASS", "특이사항 없음", DateTimeOffset.Parse("2026-11-19T09:22:00+09:00"), []),
                new("QM-003", "LOT-DEMO-B", "FAIL", "이물 의심", DateTimeOffset.Parse("2026-11-19T10:02:00+09:00"), [])
            };
        // 초기 입력도 한 트랜잭션: 중간에 실패하면 일부만 들어간 DB가 남지 않는다.
        using var transaction = connection.BeginTransaction();
        foreach (var item in seed)
        {
            Insert(connection, transaction, item);
            foreach (var row in item.Reinspections) InsertReinspection(connection, transaction, item.Id, row);
        }
        transaction.Commit();
        // Existing JSON remains intact as the migration source.
        // (위 설명과 같이) 이관 후에도 기존 JSON 파일은 변경하지 않는다.
    }
    /// <summary>새 SQLite 연결을 열어 반환한다. 호출한 쪽이 using으로 닫는다.</summary>
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
    /// <summary>전체 검사 이력을 제품 ID 순으로 읽어 반환한다. 읽기 전용이라 deferred 트랜잭션으로 일관된 시점의 스냅샷을 읽는다.</summary>
    public List<Inspection> All()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var result = Read(connection, transaction);
        transaction.Commit();
        return result;
    }
    /// <summary>
    /// 한 트랜잭션 안에서 검사 행과 재검사 행을 모두 읽어 Inspection 목록으로 조립한다.
    /// 재검사는 Sequence(입력 순서) 순으로 읽으므로 각 제품의 Reinspections 마지막 항목이 최신이다.
    /// </summary>
    /// <param name="connection">열린 연결</param>
    /// <param name="transaction">같은 트랜잭션을 써서 두 번의 SELECT가 같은 시점의 데이터를 보게 한다.</param>
    private static List<Inspection> Read(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // 1단계: 최초 검사 행을 읽어 재검사가 빈 목록인 Inspection 객체를 만든다.
        command.CommandText = "SELECT Id,Lot,InitialResult,Defect,InspectedAt FROM Inspections ORDER BY Id";
        var items = new List<Inspection>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                items.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture), []));
        // 2단계: 재검사 행을 입력 순서대로 읽어 해당 제품 객체에 붙인다(외래 키 덕분에 제품이 항상 존재).
        command.CommandText = "SELECT ProductId,RequestId,Result,Operator,Note,CreatedAt FROM Reinspections ORDER BY Sequence";
        using (var reader = command.ExecuteReader())
        {
            var byId = items.ToDictionary(x => x.Id);
            while (reader.Read())
                byId[reader.GetString(0)].Reinspections.Add(new(Guid.Parse(reader.GetString(1)), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture)));
        }
        return items;
    }
    /// <summary>
    /// 재검사 1건을 추가한다(멱등성 + 낙관적 동시성 제어).
    /// 판단 순서:
    ///  1) 제품이 없으면 404.
    ///  2) 같은 RequestId가 이미 저장돼 있으면: 제품/내용이 같으면 "이미 저장됨"으로 보고 200(재시도 성공),
    ///     다르면 409(같은 요청 ID로 다른 내용 금지).
    ///  3) 새 요청이면 ExpectedRevision이 현재 재검사 횟수와 같아야 하고, 다르면 409(다른 검사자가 먼저 변경).
    ///  4) 통과하면 INSERT 후 커밋하고 갱신된 검사를 200으로 반환.
    /// </summary>
    /// <param name="id">제품 ID(URL 경로 값)</param>
    /// <param name="request">이미 Program.cs에서 형식 검증을 마친 요청</param>
    /// <returns>200 갱신된 Inspection / 404 / 409 {error}</returns>
    public IResult Append(string id, ReinspectionRequest request)
    {
        using var connection = Open();
        // 읽기부터 쓰기까지를 한 트랜잭션으로 묶어 "읽은 뒤 다른 요청이 끼어드는" 틈을 줄인다.
        // (All()은 읽기 전용이라 deferred: true를 쓰고, 이쪽은 쓰기를 하므로 기본 트랜잭션을 쓴다.
        //  동시에 두 저장 요청이 오면 한쪽만 성공하고 다른 쪽은 최신 버전을 보고 409가 되는 것을 회귀 테스트가 확인한다.)
        using var transaction = connection.BeginTransaction();
        var items = Read(connection, transaction);
        var item = items.FirstOrDefault(x => x.Id == id);
        if (item is null) return Results.NotFound(new { error = "제품을 찾을 수 없습니다." });
        // 멱등성 검사: 이 RequestId로 이미 저장된 재검사가 있는지 전체에서 찾는다(owner = 그 재검사를 가진 제품).
        var owner = items.FirstOrDefault(x => x.Reinspections.Any(r => r.RequestId == request.RequestId));
        var previous = owner?.Reinspections.First(x => x.RequestId == request.RequestId);
        if (previous is not null)
        {
            // 같은 요청 ID가 다른 제품이거나 다른 내용이면 충돌. 비교는 저장 때와 같이 Trim한 값으로 한다.
            // 내용이 완전히 같으면 네트워크 재시도라고 보고 새로 저장하지 않은 채 현재 상태를 성공(200)으로 돌려준다.
            if (owner!.Id != id || previous.Result != request.Result || previous.Operator != request.Operator.Trim()
                || previous.Note != request.Note.Trim())
                return Results.Conflict(new { error = "같은 요청 ID로 다른 내용을 저장할 수 없습니다." });
            return Results.Ok(item);
        }
        // 버전 충돌 검사: 사용자가 화면에서 본 재검사 횟수와 현재 횟수가 다르면 다른 사람이 먼저 기록한 것이므로 저장하지 않고 409.
        if (item.Revision != request.ExpectedRevision)
            return Results.Conflict(new { error = "다른 검사자가 기록을 변경했습니다. 새로고침 후 확인하세요." });
        // 저장 시각은 서버 UTC 시각을 쓴다(클라이언트 시계에 의존하지 않음).
        var row = new Reinspection(request.RequestId, request.Result, request.Operator.Trim(), request.Note.Trim(), DateTimeOffset.UtcNow);
        InsertReinspection(connection, transaction, id, row);
        transaction.Commit();
        // 커밋은 성공했으므로 DB를 다시 읽지 않고 메모리의 객체에 새 행을 더한 결과를 반환한다.
        return Results.Ok(item with { Reinspections = [..item.Reinspections, row] });
    }
    /// <summary>
    /// VisionQC CSV에서 만든 검사 목록을 가져온다. 전부 저장하거나 전부 저장하지 않는다(원자적).
    /// 항목별 처리:
    ///  - 같은 Id가 DB에 없음: 새로 추가(added).
    ///  - 같은 Id가 있고 내용(LOT, 최초 판정, 관찰, 검사 시각)이 같음: 중복으로 보고 건너뜀(skipped). 같은 파일을 다시 올려도 안전.
    ///  - 같은 Id가 있는데 내용이 다름: 충돌. 즉시 409로 끝내며 커밋하지 않으므로 이미 처리한 앞쪽 행도 모두 취소된다.
    /// </summary>
    /// <param name="incoming">VisionCsv.Parse가 만든 검사 목록</param>
    /// <returns>200 {added, skipped} / 409 {error}</returns>
    public IResult Import(List<Inspection> incoming)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        // 기존 검사를 Id로 빠르게 찾기 위한 사전. 새로 넣은 항목도 사전에 더해 파일 내 후속 행과 비교되게 한다.
        var existing = Read(connection, transaction).ToDictionary(x => x.Id);
        var added = 0;
        foreach (var item in incoming)
        {
            if (existing.TryGetValue(item.Id, out var old))
            {
                if (old.Lot != item.Lot || old.InitialResult != item.InitialResult ||
                    old.Defect != item.Defect || old.InspectedAt != item.InspectedAt)
                    // 여기서 return하면 트랜잭션이 Commit되지 않은 채 using이 끝나 롤백된다 -> 가져오기 전체 취소.
                    return Results.Conflict(new { error = $"기존 검사 {item.Id}와 내용이 다릅니다. 파일 전체를 저장하지 않았습니다." });
                continue;
            }
            Insert(connection, transaction, item);
            existing[item.Id] = item;
            added++;
        }
        transaction.Commit();
        // 모든 행이 문제없을 때만 여기에 도달해 커밋된다.
        return Results.Ok(new { added, skipped = incoming.Count - added });
    }
    /// <summary>Inspections 테이블에 최초 검사 1행을 넣는다. 값은 매개변수($id 등)로 바인딩해 SQL 삽입을 막고, 시각은 ISO 8601("O") 문자열로 저장한다.</summary>
    private static void Insert(SqliteConnection connection, SqliteTransaction transaction, Inspection item)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO Inspections(Id,Lot,InitialResult,Defect,InspectedAt) VALUES($id,$lot,$result,$defect,$at)";
        command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$lot", item.Lot);
        command.Parameters.AddWithValue("$result", item.InitialResult);
        command.Parameters.AddWithValue("$defect", item.Defect);
        command.Parameters.AddWithValue("$at", item.InspectedAt.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }
    /// <summary>Reinspections 테이블에 재검사 1행을 넣는다. RequestId가 이미 있으면 UNIQUE 제약으로 DB가 거부한다.</summary>
    private static void InsertReinspection(SqliteConnection connection, SqliteTransaction transaction, string id, Reinspection row)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO Reinspections(ProductId,RequestId,Result,Operator,Note,CreatedAt) VALUES($id,$request,$result,$operator,$note,$at)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$request", row.RequestId.ToString());
        command.Parameters.AddWithValue("$result", row.Result);
        command.Parameters.AddWithValue("$operator", row.Operator);
        command.Parameters.AddWithValue("$note", row.Note);
        command.Parameters.AddWithValue("$at", row.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }
}

