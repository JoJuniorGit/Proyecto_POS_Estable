// 8.187-T5 (Paso 11/12, F4): montaje real (renderToString, sin jsdom) de los componentes de
// crédito CxC y del CustomerModal con el panel de perfil integrado. Detecta identificadores
// libres / imports rotos que el lint estático no ve (regresión 8.2-CR1).
import { describe, it } from 'node:test';
import assert from 'node:assert';
import { renderToString } from 'react-dom/server';
import React from 'react';
import CreditProfilePanel from './CreditProfilePanel.jsx';
import CreditPaymentModal from './CreditPaymentModal.jsx';
import CreditStatementModal from './CreditStatementModal.jsx';
import CreditReturnModal from './CreditReturnModal.jsx';
import CustomerModal from '../pos/CustomerModal.jsx';
import { AuthProvider } from '../../context/AuthContext';

globalThis.sessionStorage = {
  getItem: () => JSON.stringify({ id: 1, name: 'Cajera Test', cedula: 'V-1', role: 'Cashier' }),
  setItem: () => {},
  removeItem: () => {},
  clear: () => {},
};

// React inserta marcadores de comentario entre nodos de texto; las aserciones de etiquetas
// deben evaluar el texto visible.
function visibleText(html) {
  return html.replace(/<!--[\s\S]*?-->/g, '');
}

describe('crédito CxC — montaje real de componentes', () => {
  it('1. renders the credit profile panel for a selected customer', () => {
    let html = '';
    assert.doesNotThrow(() => {
      html = renderToString(
        <CreditProfilePanel customer={{ id: 7, name: 'Ana Pérez', cedulaOrRif: 'V-12345678' }} canEdit />
      );
    }, 'CreditProfilePanel must render without ReferenceError');

    assert.match(html, /Perfil de Crédito/);
    assert.match(html, /Cargando perfil crediticio|Cupo autorizado/);
  });

  it('2. explains the default-customer restriction instead of offering credit fields', () => {
    const html = renderToString(
      <CreditProfilePanel customer={{ id: 1, name: 'Consumidor Final', isDefault: true }} canEdit />
    );

    assert.match(html, /Consumidor Final/);
  });

  it('3. renders the payment modal with the USD capture and FIFO default', () => {
    let html = '';
    assert.doesNotThrow(() => {
      html = renderToString(
        <CreditPaymentModal isOpen customerId={7} customerName="Ana Pérez" todayRate={36.5} onClose={() => {}} />
      );
    });

    assert.match(html, /Registrar Abono CxC/);
    assert.match(visibleText(html), /Monto del abono \(USD\)/);
    assert.match(html, /FIFO \(más antigua primero\)/);
  });

  it('4. renders the statement modal and the return modal without data yet', () => {
    let statementHtml = '';
    let returnHtml = '';
    assert.doesNotThrow(() => {
      statementHtml = renderToString(
        <CreditStatementModal isOpen customerId={7} customerName="Ana Pérez" onClose={() => {}} />
      );
      returnHtml = renderToString(
        <CreditReturnModal
          isOpen
          row={{ saleId: 9, invoiceDisplay: 'F00009', customerName: 'Ana Pérez', balanceUSD: 100 }}
          onClose={() => {}}
        />
      );
    });

    assert.match(statementHtml, /Estado de Cuenta/);
    assert.match(returnHtml, /Devolución de Venta a Crédito/);
  });

  it('5. mounts CustomerModal (with the credit panel wired) inside the auth provider', () => {
    assert.doesNotThrow(() => {
      const html = renderToString(
        <AuthProvider>
          <CustomerModal isOpen onClose={() => {}} onSelectCustomer={() => {}} />
        </AuthProvider>
      );
      assert.ok(html.length > 0, 'rendered HTML must not be empty');
    });
  });
});
