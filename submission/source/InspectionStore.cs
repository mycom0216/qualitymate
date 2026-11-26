using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

sealed class InspectionStore
{
    private readonly string connectionString;
    public InspectionStore(IWebHostEnvironment environment, IConfiguration configuration)
    {
        var directory = configuration["DataDirectory"] ?? Path.Combine(environment.ContentRootPath, "data");
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "qualitymate.db"), ForeignKeys = true, DefaultTimeout = 10
        }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
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
        command.CommandText = "SELECT COUNT(*) FROM Inspections";
        if (Convert.ToInt32(command.ExecuteScalar()) > 0) return;
        var legacy = Path.Combine(directory, "inspections.json");
        var seed = File.Exists(legacy)
            ? JsonSerializer.Deserialize<List<Inspection>>(File.ReadAllText(legacy)) ?? throw new InvalidDataException("기존 데이터 읽기 실패")
            : new List<Inspection>
            {
                new("QM-001", "LOT-DEMO-A", "FAIL", "표면 흠집 의심", DateTimeOffset.Parse("2026-11-19T09:20:00+09:00"), []),
                new("QM-002", "LOT-DEMO-A", "PASS", "특이사항 없음", DateTimeOffset.Parse("2026-11-19T09:22:00+09:00"), []),
                new("QM-003", "LOT-DEMO-B", "FAIL", "이물 의심", DateTimeOffset.Parse("2026-11-19T10:02:00+09:00"), [])
            };
        using var transaction = connection.BeginTransaction();
        foreach (var item in seed)
        {
            Insert(connection, transaction, item);
            foreach (var row in item.Reinspections) InsertReinspection(connection, transaction, item.Id, row);
        }
        transaction.Commit();
        // Existing JSON remains intact as the migration source.
    }
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
    public List<Inspection> All()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var result = Read(connection, transaction);
        transaction.Commit();
        return result;
    }
    private static List<Inspection> Read(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Id,Lot,InitialResult,Defect,InspectedAt FROM Inspections ORDER BY Id";
        var items = new List<Inspection>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                items.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture), []));
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
    public IResult Append(string id, ReinspectionRequest request)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var items = Read(connection, transaction);
        var item = items.FirstOrDefault(x => x.Id == id);
        if (item is null) return Results.NotFound(new { error = "제품을 찾을 수 없습니다." });
        var owner = items.FirstOrDefault(x => x.Reinspections.Any(r => r.RequestId == request.RequestId));
        var previous = owner?.Reinspections.First(x => x.RequestId == request.RequestId);
        if (previous is not null)
        {
            if (owner!.Id != id || previous.Result != request.Result || previous.Operator != request.Operator.Trim()
                || previous.Note != request.Note.Trim())
                return Results.Conflict(new { error = "같은 요청 ID로 다른 내용을 저장할 수 없습니다." });
            return Results.Ok(item);
        }
        if (item.Revision != request.ExpectedRevision)
            return Results.Conflict(new { error = "다른 검사자가 기록을 변경했습니다. 새로고침 후 확인하세요." });
        var row = new Reinspection(request.RequestId, request.Result, request.Operator.Trim(), request.Note.Trim(), DateTimeOffset.UtcNow);
        InsertReinspection(connection, transaction, id, row);
        transaction.Commit();
        return Results.Ok(item with { Reinspections = [..item.Reinspections, row] });
    }
    public IResult Import(List<Inspection> incoming)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var existing = Read(connection, transaction).ToDictionary(x => x.Id);
        var added = 0;
        foreach (var item in incoming)
        {
            if (existing.TryGetValue(item.Id, out var old))
            {
                if (old.Lot != item.Lot || old.InitialResult != item.InitialResult ||
                    old.Defect != item.Defect || old.InspectedAt != item.InspectedAt)
                    return Results.Conflict(new { error = $"기존 검사 {item.Id}와 내용이 다릅니다. 파일 전체를 저장하지 않았습니다." });
                continue;
            }
            Insert(connection, transaction, item);
            existing[item.Id] = item;
            added++;
        }
        transaction.Commit();
        return Results.Ok(new { added, skipped = incoming.Count - added });
    }
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

