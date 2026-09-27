import { ShieldAlert } from 'lucide-react';

const DEFAULT_MESSAGE = 'No tienes los permisos necesarios para acceder a esta sección.';

export default function AccessDenied({ message = DEFAULT_MESSAGE }) {
  return (
    <div className="p-4 text-center mt-5">
      <ShieldAlert size={48} className="color-danger mx-auto mb-3" />
      <h3 className="font-bold text-lg mb-2">Acceso Denegado</h3>
      <p className="text-muted">{message}</p>
    </div>
  );
}
