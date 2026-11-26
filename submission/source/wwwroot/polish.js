(() => {
  const motion = matchMedia('(pointer: fine) and (prefers-reduced-motion: no-preference)');
  let frame = 0;
  let pointerX = 0;
  let pointerY = 0;
  let surface = null;

  document.addEventListener('pointermove', event => {
    if (!motion.matches || event.pointerType === 'touch') return;
    pointerX = event.clientX;
    pointerY = event.clientY;
    surface = event.target.closest?.('.card, .metrics article, header') || null;
    if (frame) return;
    frame = requestAnimationFrame(() => {
      frame = 0;
      if (!motion.matches) return;
      document.documentElement.style.setProperty('--pointer-x', pointerX + 'px');
      document.documentElement.style.setProperty('--pointer-y', pointerY + 'px');
      if (surface) {
        const bounds = surface.getBoundingClientRect();
        surface.style.setProperty('--surface-x', (pointerX - bounds.left) + 'px');
        surface.style.setProperty('--surface-y', (pointerY - bounds.top) + 'px');
      }
    });
  }, { passive: true });
})();

