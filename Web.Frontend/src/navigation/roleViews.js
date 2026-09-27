export const VIEW_ORDER = Object.freeze([
  'pos',
  'pending',
  'pickups',
  'catalog',
  'history',
  'register',
  'closing',
  'settings',
  'exchange',
]);

export const ROLE_VIEWS = Object.freeze({
  Admin: Object.freeze(['pos', 'pending', 'pickups', 'catalog', 'history', 'register', 'closing', 'settings', 'exchange']),
  Manager: Object.freeze(['pos', 'pending', 'pickups', 'catalog', 'history', 'register', 'closing', 'settings', 'exchange']),
  Cashier: Object.freeze(['pos', 'pending', 'pickups', 'catalog', 'history', 'register', 'closing']),
  Driver: Object.freeze(['pickups', 'history']),
});

const ROLE_ALIASES = Object.freeze({
  '1': 'Cashier',
  '2': 'Manager',
  '3': 'Admin',
  '4': 'Driver',
  cashier: 'Cashier',
  manager: 'Manager',
  admin: 'Admin',
  driver: 'Driver',
});

const EMPTY_VIEWS = Object.freeze([]);

export function normalizeRole(role) {
  if (role === null || role === undefined) return null;
  const key = String(role).trim().toLowerCase();
  return ROLE_ALIASES[key] ?? null;
}

export function getAllowedViews(role) {
  const canonical = normalizeRole(role);
  return canonical ? ROLE_VIEWS[canonical] : EMPTY_VIEWS;
}

export function isValidView(view) {
  return VIEW_ORDER.includes(view);
}

export function canAccessView(role, view) {
  return getAllowedViews(role).includes(view);
}

export function getFallbackView(role) {
  const allowed = getAllowedViews(role);
  return allowed.length > 0 ? allowed[0] : null;
}

export function resolveAccessibleView(role, requestedView) {
  if (canAccessView(role, requestedView)) {
    return { allowed: true, view: requestedView };
  }
  return { allowed: false, view: getFallbackView(role) };
}
