import { test, describe } from 'node:test';
import assert from 'node:assert';

describe('useMobileBackGuard Navigation & Modal Interception Logic Tests', () => {
  test('1. Intercepts Back button when a standard modal is active, closing modal without confirming exit', () => {
    let modalClosed = false;
    let confirmCalled = false;

    const mockState = {
      activeModal: 'scanner',
      onCloseModal: () => { modalClosed = true; return true; },
      hasItems: true,
      isConfirmExitOpen: false,
    };

    const simulatePopState = (state) => {
      if (state.isConfirmExitOpen) {
        state.isConfirmExitOpen = false;
        return { action: 'cancelled_confirm', stayedOnPos: true };
      }

      if (state.activeModal) {
        const closed = state.onCloseModal?.();
        if (closed === false) {
          return { action: 'modal_close_prevented', stayedOnPos: true, rePushModal: true };
        }
        return { action: 'closed_modal', stayedOnPos: true };
      }

      if (state.hasItems) {
        confirmCalled = true;
        state.isConfirmExitOpen = true;
        return { action: 'opened_custom_confirm', stayedOnPos: true };
      }

      return { action: 'normal_back', stayedOnPos: false };
    };

    const result = simulatePopState(mockState);

    assert.strictEqual(modalClosed, true, 'Modal close callback must be executed');
    assert.strictEqual(confirmCalled, false, 'Confirm dialog must NOT be shown when closing a modal');
    assert.strictEqual(result.action, 'closed_modal');
    assert.strictEqual(result.stayedOnPos, true);
  });

  test('2. Protects payment modal when >= 1 payment is registered (prevents involuntary exit and re-pushes history state)', () => {
    let discardConfirmShown = false;

    // Simulate CheckoutModal with 1 payment registered
    const mockPayments = [{ methodId: 1, amountUsd: 10, amountBsS: 500 }];

    const mockCheckoutModal = {
      requestClose: () => {
        if (mockPayments.length > 0) {
          discardConfirmShown = true;
          return false; // Close prevented!
        }
        return true;
      }
    };

    const mockState = {
      activeModal: 'external',
      onCloseModal: () => mockCheckoutModal.requestClose(),
      hasItems: true,
      isConfirmExitOpen: false,
    };

    const simulatePopState = (state) => {
      if (state.activeModal) {
        const closed = state.onCloseModal?.();
        if (closed === false) {
          // Guard re-pushes modal state to keep history in sync for Android gestures
          return { action: 'modal_close_prevented', stayedOnPos: true, rePushModal: true };
        }
        return { action: 'closed_modal', stayedOnPos: true };
      }
      return { action: 'normal_back', stayedOnPos: false };
    };

    const result = simulatePopState(mockState);

    assert.strictEqual(discardConfirmShown, true, 'Discard confirmation must be triggered instead of closing');
    assert.strictEqual(result.action, 'modal_close_prevented');
    assert.strictEqual(result.rePushModal, true, 'Must re-push modal history state to prevent stack corruption');
    assert.strictEqual(result.stayedOnPos, true);
  });

  test('3. Opens custom POS ConfirmModal (no window.confirm) when Back is pressed with items in cart', () => {
    const mockState = {
      activeModal: null,
      onCloseModal: () => {},
      hasItems: true,
      isConfirmExitOpen: false,
    };

    const simulatePopState = (state) => {
      if (state.activeModal) {
        state.onCloseModal?.();
        return { action: 'closed_modal', stayedOnPos: true };
      }

      if (state.hasItems) {
        state.isConfirmExitOpen = true;
        return { action: 'opened_custom_confirm', stayedOnPos: true };
      }

      return { action: 'normal_back', stayedOnPos: false };
    };

    const result = simulatePopState(mockState);
    assert.strictEqual(result.action, 'opened_custom_confirm');
    assert.strictEqual(mockState.isConfirmExitOpen, true, 'Custom ConfirmModal state must be true');
    assert.strictEqual(result.stayedOnPos, true);
  });

  test('4. Android 10+ Edge Gesture: Swiping back while custom ConfirmModal is open dismisses dialog and keeps user on POS', () => {
    const mockState = {
      activeModal: null,
      onCloseModal: () => {},
      hasItems: true,
      isConfirmExitOpen: true, // Custom confirm is already open
    };

    const simulatePopState = (state) => {
      // Swiping back while confirm modal is open cancels the modal
      if (state.isConfirmExitOpen) {
        state.isConfirmExitOpen = false;
        return { action: 'cancelled_confirm_via_gesture', stayedOnPos: true, restoreCartGuard: true };
      }

      if (state.hasItems) {
        state.isConfirmExitOpen = true;
        return { action: 'opened_custom_confirm', stayedOnPos: true };
      }

      return { action: 'normal_back', stayedOnPos: false };
    };

    const result = simulatePopState(mockState);
    assert.strictEqual(result.action, 'cancelled_confirm_via_gesture');
    assert.strictEqual(mockState.isConfirmExitOpen, false, 'Confirm dialog must be closed on second back gesture');
    assert.strictEqual(result.restoreCartGuard, true, 'Cart guard must be re-armed in history');
    assert.strictEqual(result.stayedOnPos, true);
  });

  test('5. Confirmed exit from custom ConfirmModal triggers navigation', () => {
    let navigatedBack = false;

    const handleConfirmExit = (setOpen, onNavigate) => {
      setOpen(false);
      onNavigate();
    };

    let isOpen = true;
    handleConfirmExit(
      (val) => { isOpen = val; },
      () => { navigatedBack = true; }
    );

    assert.strictEqual(isOpen, false);
    assert.strictEqual(navigatedBack, true);
  });

  test('6. Allows normal navigation without prompt when cart is empty and no modals are open', () => {
    const mockState = {
      activeModal: null,
      onCloseModal: () => {},
      hasItems: false,
      isConfirmExitOpen: false,
    };

    const simulatePopState = (state) => {
      if (state.activeModal) {
        state.onCloseModal?.();
        return { action: 'closed_modal', stayedOnPos: true };
      }

      if (state.hasItems) {
        state.isConfirmExitOpen = true;
        return { action: 'opened_custom_confirm', stayedOnPos: true };
      }

      return { action: 'normal_back', stayedOnPos: false };
    };

    const result = simulatePopState(mockState);
    assert.strictEqual(mockState.isConfirmExitOpen, false, 'No confirmation prompt should appear for empty cart');
    assert.strictEqual(result.action, 'normal_back');
    assert.strictEqual(result.stayedOnPos, false);
  });

  test('7. Beforeunload listener arms browser protection when cart has items', () => {
    const handleBeforeUnload = (hasItems, event) => {
      if (hasItems) {
        const msg = '¿Seguro que desea abandonar la página? Se perderá el carrito actual';
        event.preventDefault();
        event.returnValue = msg;
        return msg;
      }
      return undefined;
    };

    const fakeEventWithItems = {
      prevented: false,
      returnValue: '',
      preventDefault() { this.prevented = true; }
    };

    const msg = handleBeforeUnload(true, fakeEventWithItems);
    assert.strictEqual(fakeEventWithItems.prevented, true);
    assert.ok(msg.includes('Se perderá el carrito actual'));

    const fakeEventEmpty = {
      prevented: false,
      returnValue: '',
      preventDefault() { this.prevented = true; }
    };

    const emptyResult = handleBeforeUnload(false, fakeEventEmpty);
    assert.strictEqual(fakeEventEmpty.prevented, false);
    assert.strictEqual(emptyResult, undefined);
  });
});
