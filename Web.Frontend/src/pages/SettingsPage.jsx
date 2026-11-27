import { useState, useEffect, useRef } from 'react';
import { api } from '../services/api';
import { getAllPaymentMethods } from '../services/paymentApi';
import { Settings, Power, Loader2, DollarSign, CreditCard, Wifi } from 'lucide-react';
import { useCurrencyFormat } from '../context/CurrencyFormatContext';
import ConfirmModal from '../components/ui/ConfirmModal';
import SettingsPairing from './SettingsPairing';
import SettingsCurrencyFormat from './SettingsCurrencyFormat';
import SettingsPaymentMethods from './SettingsPaymentMethods';
import PaymentMethodFeeRules from '../components/settings/PaymentMethodFeeRules';
import { SETTINGS_SECTIONS, resolveTabIndex } from './settingsSections';
import { useMediaQuery } from '../utils/useMediaQuery';
import RoleGuard from '../navigation/RoleGuard';
import './SettingsPage.css';

const SECTION_ICONS = {
  currency: DollarSign,
  payments: CreditCard,
  network: Wifi,
  system: Power,
};

export default function SettingsPage() {
  return (
    <RoleGuard view="settings" message="No tienes los permisos necesarios para acceder a la Configuración del Sistema.">
      <SettingsPageContent />
    </RoleGuard>
  );
}

function SettingsPageContent() {
  const { currencyFormat, setCurrencyFormat, formatBsS, formatUSD } = useCurrencyFormat();
  const [methods, setMethods] = useState([]);
  const [loadingMethods, setLoadingMethods] = useState(false);
  const [message, setMessage] = useState(null);
  const [restarting, setRestarting] = useState(false);
  const [restartModalOpen, setRestartModalOpen] = useState(false);
  const [pairingInfo, setPairingInfo] = useState(null);
  const [activeSection, setActiveSection] = useState(SETTINGS_SECTIONS[0].id);
  const tabRefs = useRef([]);
  const compactNav = useMediaQuery('(max-width: 980px)');

  const handleTabKeyDown = (event, index) => {
    const nextIndex = resolveTabIndex(index, event.key);
    if (nextIndex === null) return;

    event.preventDefault();
    setActiveSection(SETTINGS_SECTIONS[nextIndex].id);
    tabRefs.current[nextIndex]?.focus();
  };

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
        text: err?.message || 'Error al actualizar el formato de moneda.',
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
        text: err?.message || 'Error al reiniciar el sistema. El servicio podría ya estar reiniciando.',
      });
    } finally {
      setRestarting(false);
    }
  };

  return (
    <div className="settings-page set-page">
      <header className="set-hero">
        <div className="set-hero-main">
          <span className="set-hero-badge" aria-hidden="true">
            <Settings size={22} />
          </span>
          <div className="set-hero-copy">
            <h2 className="set-hero-title">Configuración del Sistema</h2>
            <p className="set-hero-subtitle">
              Gestione las preferencias globales, conectividad, métodos de pago y mantenimiento del sistema.
            </p>
          </div>
        </div>
        <div className="set-meta-pill">
          <span className="set-meta-dot" aria-hidden="true" />
          Configuración global
        </div>
      </header>

      {message && (
        <div className={`set-alert set-alert--${message.type}`} role="status">
          {message.text}
        </div>
      )}

      <div className="set-layout">
        <nav className="set-nav" aria-label="Navegación de configuración">
          <div className="set-nav-head">
            <span className="set-nav-kicker">SECCIONES</span>
            <span className="set-nav-count">{String(SETTINGS_SECTIONS.length).padStart(2, '0')}</span>
          </div>

          <div
            className="set-nav-tabs"
            role="tablist"
            aria-label="Secciones de Configuración"
            aria-orientation={compactNav ? 'horizontal' : 'vertical'}
          >
            {SETTINGS_SECTIONS.map((section, index) => {
              const Icon = SECTION_ICONS[section.icon];
              const isActive = activeSection === section.id;

              return (
                <button
                  key={section.id}
                  ref={(element) => {
                    tabRefs.current[index] = element;
                  }}
                  type="button"
                  role="tab"
                  id={`settings-tab-${section.id}`}
                  aria-selected={isActive}
                  aria-controls={`settings-panel-${section.id}`}
                  tabIndex={isActive ? 0 : -1}
                  className={`set-tab${isActive ? ' is-active' : ''}`}
                  onClick={() => setActiveSection(section.id)}
                  onKeyDown={(event) => handleTabKeyDown(event, index)}
                >
                  <span className="set-tab-icon" aria-hidden="true">
                    <Icon size={16} />
                  </span>
                  <span className="set-tab-copy">
                    <span className="set-tab-label">{section.label}</span>
                    <span className="set-tab-desc">{section.description}</span>
                  </span>
                </button>
              );
            })}
          </div>

          <p className="set-nav-foot">Los cambios se aplican de inmediato en este equipo.</p>
        </nav>

        <div className="set-panels">
          <section
            role="tabpanel"
            id="settings-panel-currency"
            aria-labelledby="settings-tab-currency"
            hidden={activeSection !== 'currency'}
            className="set-panel"
          >
            <SettingsCurrencyFormat
              currencyFormat={currencyFormat}
              handleFormatChange={handleFormatChange}
              formatBsS={formatBsS}
              formatUSD={formatUSD}
            />
          </section>

          <section
            role="tabpanel"
            id="settings-panel-payments"
            aria-labelledby="settings-tab-payments"
            hidden={activeSection !== 'payments'}
            className="set-panel"
          >
            <SettingsPaymentMethods
              methods={methods}
              setMethods={setMethods}
              setMessage={setMessage}
              loadMethods={loadMethods}
              loadingMethods={loadingMethods}
            />
            <PaymentMethodFeeRules methods={methods} setMessage={setMessage} />
          </section>

          <section
            role="tabpanel"
            id="settings-panel-network"
            aria-labelledby="settings-tab-network"
            hidden={activeSection !== 'network'}
            className="set-panel"
          >
            <SettingsPairing pairingInfo={pairingInfo} setPairingInfo={setPairingInfo} />
          </section>

          <section
            role="tabpanel"
            id="settings-panel-system"
            aria-labelledby="settings-tab-system"
            hidden={activeSection !== 'system'}
            className="set-panel"
          >
            <p className="set-section-label set-section-label--warning">ZONA DE ACCIONES CRÍTICAS</p>
            <div className="set-card set-critical-card">
              <div className="set-critical-main">
                <span className="set-card-icon set-card-icon--warning" aria-hidden="true">
                  <Power size={18} />
                </span>
                <div className="set-critical-copy">
                  <div className="set-critical-title-row">
                    <h3 className="set-card-title">Reiniciar Sistema</h3>
                    <span className="set-status-pill set-status-pill--warning">Acción crítica</span>
                  </div>
                  <p className="set-card-subtitle">
                    Reinicie el servicio del sistema POS cuando presente lentitud o comportamiento anómalo.
                    Las ventas y la configuración se conservan; la caja se restablece en segundos.
                  </p>
                </div>
              </div>
              <div className="set-critical-action">
                <button
                  type="button"
                  className="set-btn-warning"
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
          </section>
        </div>
      </div>

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
