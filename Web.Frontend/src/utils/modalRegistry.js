let openModalCount = 0;

export function registerOpenModal() {
  openModalCount += 1;

  let released = false;
  return () => {
    if (released) return;
    released = true;
    openModalCount = Math.max(0, openModalCount - 1);
  };
}

export function hasOpenModals() {
  return openModalCount > 0;
}

export function resetOpenModals() {
  openModalCount = 0;
}
