// =====================================================================
// polish.js  -  장식용 포인터 조명 효과 (기능과 무관)
// =====================================================================
// 역할: 마우스 위치를 CSS 변수(--pointer-x/y, --surface-x/y)로 전달해 polish.css의 빛 번짐 효과를 만든다.
//       서버 통신이나 데이터 처리는 하지 않으며, 이 파일이 없어도 앱은 정상 동작한다.
// 조건: 마우스 같은 정밀 포인터이고 '동작 줄이기' 설정이 꺼져 있을 때만 동작한다(접근성 배려).
// =====================================================================
(() => {
  // 효과 허용 조건: 정밀 포인터(마우스) + 사용자가 동작 감소를 요구하지 않음. 조건이 바뀌면 matches 값도 자동으로 바뀐다.
  const motion = matchMedia('(pointer: fine) and (prefers-reduced-motion: no-preference)');
  // frame: 예약된 애니메이션 프레임 ID(0이면 예약 없음). 이동 이벤트가 매우 잦아도 프레임당 한 번만 계산하게 한다.
  let frame = 0;
  let pointerX = 0;
  let pointerY = 0;
  let surface = null;

  document.addEventListener('pointermove', event => {
    if (!motion.matches || event.pointerType === 'touch') return;
    pointerX = event.clientX;
    pointerY = event.clientY;
    // 포인터가 카드/요약/헤더 위에 있으면 그 요소를 기억해 요소 내부 좌표(--surface-x/y)도 갱신한다.
    surface = event.target.closest?.('.card, .metrics article, header') || null;
    if (frame) return;
    // 화면 갱신 주기에 맞춰 한 번에 CSS 변수를 쓴다(성능). 터치 입력은 위에서 제외한다.
    frame = requestAnimationFrame(() => {
      frame = 0;
      if (!motion.matches) return;
      // 페이지 전체 기준 포인터 좌표.
      document.documentElement.style.setProperty('--pointer-x', pointerX + 'px');
      document.documentElement.style.setProperty('--pointer-y', pointerY + 'px');
      if (surface) {
        // 요소 왼쪽 위 기준 좌표로 변환해 요소 안의 빛 위치를 정한다.
        const bounds = surface.getBoundingClientRect();
        surface.style.setProperty('--surface-x', (pointerX - bounds.left) + 'px');
        surface.style.setProperty('--surface-y', (pointerY - bounds.top) + 'px');
      }
    });
  }, { passive: true });
})();

