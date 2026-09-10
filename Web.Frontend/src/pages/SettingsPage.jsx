import { useState, useEffect } from 'react';
import { api } from '../services/api';
import { getAllPaymentMethods } from '../services/paymentApi';
import { Settings, Power, Loader2 } from 'lucide-react';
import { useCurrencyFormat } from '../context/CurrencyFormatContext';
import ConfirmModal from '../components/ui/ConfirmModal';
import SettingsPairing from './SettingsPairing';
import SettingsCurrencyFormat from './SettingsCurrencyFormat';
import SettingsPaymentMethods from './SettingsPaymentMethods';
import './SettingsPage.css';

export default function SettingsPage() {
  const { currencyFormat, setCurrencyFormat, formatBsS, formatUSD } = useCurrencyFormat();
  const [methods, setMethods] = useState([]);
  const [loadingMethods, setLoadingMethods] = useState(false);
  const [message, setMessage] = useState(null);
  const [restarting, setRestarting] = useState(false);
  const [restartModalOpen, setRestartModalOpen] = useState(false);
  const [pairingInfo, setPairingInfo] = useState(null);

  const handleFormatChange = async (newFmt) => {
    try {
      await setCurrencyFormat(newFmt);
      setMessage({
        type: 'success',
        text: `Formato de moneda actualizado a "${newFmt === 'Venezuelan' ? 'Venezolano Contable (1.234,56)' : 'Internacional (1,234.56)'}".`,
      });
      setTimeout(() => setMessage(null), 3500);
    } catch (err) {
      setMessage({
        type: 'danger',
        text: err?.response?.data?.message || err?.message || 'Error al actualizar el formato de moneda.',
      });
    }
  };

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

    const handleMethodsUpdated = () => {
      loadMethods();
    };
    window.addEventListener('onPaymentMethodsUpdated', handleMethodsUpdated);
    return () => {
      window.removeEventListener('onPaymentMethodsUpdated', handleMethodsUpdated);
    };
  }, []);

  const handleRestartSystem = async () => {
    setRestartModalOpen(false);
    setRestarting(true);
    try {
      await api.post('/api/administration/restart');
      setMessage({
        type: 'success',
        text: 'Reinicio del servicio iniciado. La conexión se restablecerá en unos segundos.',
      });
    } catch (err) {
      console.error('[SettingsPage] Error reiniciando sistema:', err);
      setMessage({
        type: 'danger',
        text: err?.response?.data?.message || err?.message || 'Error al reiniciar el sistema. El servicio podría ya estar reiniciando.',
      });
    } finally {
      setRestarting(false);
    }
  };

  return (
    <div className="settings-page set-page">
      <h2 className="page-title mb-4 font-bold text-xl sm:text-2xl flex-align-center gap-2">
        <Settings size={24} className="color-primary flex-shrink-0" />
        <span>Configuración del Sistema</span>
      </h2>

      {message && <div className={`alert alert-${message.type} mb-4 text-sm p-3`}>{message.text}</div>}

      <SettingsPairing pairingInfo={pairingInfo} setPairingInfo={setPairingInfo} />

      <SettingsCurrencyFormat
        currencyFormat={currencyFormat}
        handleFormatChange={handleFormatChange}
        formatBsS={formatBsS}
        formatUSD={formatUSD}
      />

      <div className="card mb-4 p-3 sm:p-4">
        <div className="flex-between flex-align-center flex-wrap gap-3">
          <div className="flex-1 min-w-0">
            <h3 className="card-title flex-align-center gap-2 text-base font-bold mb-2">
              <Power size={20} className="color-primary flex-shrink-0" />
              <span>Reiniciar Sistema</span>
            </h3>
            <p className="text-muted text-xs sm:text-sm mb-0 set-restart-hint">
              Reinicie el servicio del sistema POS cuando presente lentitud o comportamiento anómalo.
              Las ventas y la configuración se conservan; la caja se restablece en segundos.
            </p>
          </div>
          <button
            type="button"
            className="btn btn-primary flex-align-center gap-1 text-sm flex-shrink-0"
            onClick={() => setRestartModalOpen(true)}
            disabled={restarting}
            title="Reiniciar el servicio del sistema POS"
            aria-label="Reiniciar el servicio del sistema POS"
          >
            {restarting ? <Loader2 size={16} className="animate-spin" /> : <Power size={16} />}
            <span>{restarting ? 'Reiniciando...' : 'Reiniciar Sistema'}</span>
          </button>
        </div>
      </div>

      <SettingsPaymentMethods methods={methods} setMethods={setMethods} setMessage={setMessage} loadMethods={loadMethods} loadingMethods={loadingMethods} />

      <ConfirmModal
        isOpen={restartModalOpen}
        onClose={() => setRestartModalOpen(false)}
        onConfirm={handleRestartSystem}
        title="¿Reiniciar el sistema POS?"
        message="El servicio se reiniciará durante unos segundos. Ninguna venta ni configuración se perderá, pero las operaciones en curso se interrumpirán brevemente."
        confirmText="Reiniciar Sistema"
        cancelText="Cancelar"
        variant="primary"
        icon={<Power size={20} />}
      />
    </div>
  );
}