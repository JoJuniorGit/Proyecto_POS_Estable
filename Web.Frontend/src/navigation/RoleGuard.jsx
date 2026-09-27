import { useAuth } from '../context/AuthContext';
import AccessDenied from './AccessDenied';
import { canAccessView } from './roleViews';

export default function RoleGuard({ view, message, children }) {
  const { user } = useAuth();

  if (!canAccessView(user?.role, view)) {
    return <AccessDenied message={message} />;
  }

  return children;
}
