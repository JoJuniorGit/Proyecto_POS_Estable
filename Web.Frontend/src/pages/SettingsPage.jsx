import { useState, useEffect } from 'react';
import { api } from '../services/api';
import { getAllPaymentMethods } from '../services/paymentApi';
import { Settings, CreditCard, Plus, Save, Loader2, Check, X } from 'lucide-react';

export default function SettingsPage() {
  const [methods, setMethods] = useState([]);
  const [loadingMethods, setLoadingMethods] = useState(false);
  const [newMethodName, setNewMethodName] = useState('');
  const [newMethodIsCash, setNewMethodIsCash] = useState(false);
  const [newMethodRequiresRef, setNewMethodRequiresRef] = useState(false);
  const [message, setMessage] = useState(null);

  const loadMethods = async () => {
    setLoadingMethods(true);
    try {
      const data = await getAllPaymentMethods();
      setMethods(data || []);
    } catch (err) {
      console.error('[SettingsPage] Error cargando métodos de pago:', err);
    } finally {
      setLoadingMethods(false);
    }
  };

  useEffect(() => {
    loadMethods();
  }, []);

  const handleToggleActive = async (method) => {
    try {
      const updated = { ...method, isActive: !method.isActive };
      await api.put(`/api/paymentmethods/${method.id}`, updated);
      setMethods((prev) => prev.map((m) => (m.id === method.id ? updated : m)));
    } catch (err) {
      console.error('[SettingsPage] Error actualizando método:', err);
      setMessage({ type: 'danger', text: 'Error al cambiar estado del método.' });
    }
  };

  const handleToggleRef = async (method) => {
    try {
      const updated = { ...method, requiresReference: !method.requiresReference };
      await api.put(`/api/paymentmethods/${method.id}`, updated);
      setMethods((prev) => prev.map((m) => (m.id === method.id ? updated : m)));
    } catch (err) {
      console.error('[SettingsPage] Error actualizando método:', err);
      setMessage({ type: 'danger', text: 'Error al actualizar configuración de referencia.' });
    }
  };

  const handleAddMethod = async (e) => {
    e.preventDefault();
    if (!newMethodName.trim()) return;

    try {
      const dto = {
        name: newMethodName.trim(),
        isActive: true,
        isCash: newMethodIsCash,
        requiresReference: newMethodRequiresRef,
      };
      await api.post('/api/paymentmethods', dto);
      setNewMethodName('');
      setNewMethodIsCash(false);
      setNewMethodRequiresRef(false);
      setMessage({ type: 'success', text: 'Nuevo método de pago agregado.' });
      loadMethods();
    } catch (err) {
      console.error('[SettingsPage] Error agregando método:', err);
      setMessage({ type: 'danger', text: 'Error al crear método de pago.' });
    }
  };

  return (
    <div className="settings-page" style={{ maxWidth: '800px', margin: '0 auto', padding: '16px' }}>
      <h2 className="page-title mb-4 font-bold text-xl sm:text-2xl flex-align-center gap-2">
        <Settings size={24} className="color-primary flex-shrink-0" />
        <span>Configuración del Sistema</span>
      </h2>

      {message && <div className={`alert alert-${message.type} mb-4 text-sm p-3`}>{message.text}</div>}

      {/* Métodos de Pago */}
      <div className="card mb-4 p-3 sm:p-4">
        <h3 className="card-title mb-3 flex-align-center gap-2 text-base font-bold">
          <CreditCard size={20} className="color-primary flex-shrink-0" /> Métodos de Pago Habilitados
        </h3>

        {loadingMethods ? (
          <div className="p-4 text-center text-muted">
            <Loader2 className="animate-spin mb-2 inline-block" size={24} />
            <div>Cargando métodos de pago...</div>
          </div>
        ) : (
          <>
            {/* ── Requisito 1, 2 y 3: Tarjetas Independientes para Vista Móvil con Etiquetas de Contexto ── */}
            <div className="settings-mobile-cards-view mb-4">
              {methods.map((m) => (
                <div key={m.id} className="settings-method-card p-3 mb-3 border rounded-lg bg-surface shadow-xs">
                  
                  {/* Fila 1: Encabezado de Tarjeta (Nombre a la izquierda, Estado a la extrema derecha) */}
                  <div className="flex-between flex-align-center mb-2.5 pb-2 border-bottom">
                    <span className="font-bold text-base color-primary">{m.name}</span>
                    <button
                      type="button"
                      className={`btn btn-sm ${m.isActive ? 'btn-primary' : 'btn-danger'} text-xs font-bold px-3`}
                      onClick={() => handleToggleActive(m)}
                      style={{ borderRadius: '14px', minWidth: '76px' }}
                    >
                      {m.isActive ? 'Activo' : 'Inactivo'}
                    </button>
                  </div>

                  {/* Fila 2: Detalles Secundarios con Etiquetas de Contexto ("Tipo:" y "Requiere Ref.:") */}
                  <div className="flex-between flex-align-center text-xs">
                    <div className="flex-align-center gap-1">
                      <span className="text-muted text-xs">Tipo:</span>
                      <span className="font-medium">{m.isCash ? 'Efectivo' : 'Digital / Banco'}</span>
                    </div>

                    <div className="flex-align-center gap-1.5">
                      <span className="text-muted text-xs">Requiere Ref.:</span>
                      <button
                        type="button"
                        className={`btn btn-xs ${m.requiresReference ? 'btn-primary' : 'btn-outline'} text-xs font-bold`}
                        onClick={() => handleToggleRef(m)}
                        style={{ borderRadius: '10px', padding: '2px 10px' }}
                      >
                        {m.requiresReference ? 'Sí' : 'No'}
                      </button>
                    </div>
                  </div>

                </div>
              ))}
            </div>

            {/* Vista de Tabla Tradicional para Escritorio */}
            <div className="overflow-x-auto settings-desktop-table-view mb-4">
              <table className="cart-table mb-2">
                <thead>
                  <tr>
                    <th>Nombre</th>
                    <th className="text-center">Tipo</th>
                    <th className="text-center">Requiere Referencia</th>
                    <th className="text-center">Estado</th>
                  </tr>
                </thead>
                <tbody>
                  {methods.map((m) => (
                    <tr key={m.id}>
                      <td className="font-medium">{m.name}</td>
                      <td className="text-center">{m.isCash ? 'Efectivo' : 'Digital / Banco'}</td>
                      <td className="text-center">
                        <button
                          type="button"
                          className={`btn btn-sm ${m.requiresReference ? 'btn-primary' : 'btn-outline'}`}
                          onClick={() => handleToggleRef(m)}
                        >
                          {m.requiresReference ? 'Sí' : 'No'}
                        </button>
                      </td>
                      <td className="text-center">
                        <button
                          type="button"
                          className={`btn btn-sm ${m.isActive ? 'btn-primary' : 'btn-danger'}`}
                          onClick={() => handleToggleActive(m)}
                        >
                          {m.isActive ? 'Activo' : 'Inactivo'}
                        </button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </>
        )}

        {/* Formulario para Agregar Método */}
        <form onSubmit={handleAddMethod} className="border-top pt-3 mt-2">
          <h4 className="font-bold mb-3 text-sm sm:text-base">Agregar Nuevo Método de Pago</h4>
          <div className="form-row align-end flex-wrap gap-3">
            <div className="form-group flex-2 mb-0" style={{ minWidth: '220px' }}>
              <label className="form-label text-xs text-muted mb-1 block">Nombre del Método</label>
              <input
                type="text"
                className="form-input"
                placeholder="Ej. Pago Móvil Banesco"
                value={newMethodName}
                onChange={(e) => setNewMethodName(e.target.value)}
                required
              />
            </div>

            <div className="form-group mb-0 flex-align-center" style={{ paddingBottom: '8px' }}>
              <label className="form-label cursor-pointer flex-align-center gap-2 text-sm mb-0">
                <input
                  type="checkbox"
                  checked={newMethodIsCash}
                  onChange={(e) => setNewMethodIsCash(e.target.checked)}
                />
                <span>Es Efectivo</span>
              </label>
            </div>

            <div className="form-group mb-0 flex-align-center" style={{ paddingBottom: '8px' }}>
              <label className="form-label cursor-pointer flex-align-center gap-2 text-sm mb-0">
                <input
                  type="checkbox"
                  checked={newMethodRequiresRef}
                  onChange={(e) => setNewMethodRequiresRef(e.target.checked)}
                />
                <span>Req. Referencia</span>
              </label>
            </div>

            <div className="form-group mb-0">
              <button type="submit" className="btn btn-primary flex-center gap-2">
                <Plus size={16} /> Agregar
              </button>
            </div>
          </div>
        </form>
      </div>
    </div>
  );
}
