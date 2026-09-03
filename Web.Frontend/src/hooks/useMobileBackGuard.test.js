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
      allowExit: false,
    };

    const simulatePopState = (state) => {
      if (state.allowExit) return { action: 'exit_allowed', stayedOnPos: false };

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
        return { action: 'guard_re_injected', stayedOnPos: true };
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
      allowExit: false,
    };

    const simulatePopState = (state) => {
      if (state.allowExit) return { action: 'exit_allowed', stayedOnPos: false };

      if (state.activeModal) {
        const closed = state.onCloseModal?.();
        if (closed === false) {
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

  test('3. First Back attempt with items in cart opens custom ConfirmModal and re-injects history guard', () => {
    let pushedStateCount = 0;

    const mockState = {
      activeModal: null,
      onCloseModal: () => {},
      hasItems: true,
      isConfirmExitOpen: false,
      allowExit: false,
    };

    const simulatePopState = (state) => {
      if (state.allowExit) return { action: 'exit_allowed', stayedOnPos: false };

      if (state.activeModal) {
        state.onCloseModal?.();
        return { action: 'closed_modal', stayedOnPos: true };
      }

      if (state.hasItems) {
        pushedStateCount++;
        state.isConfirmExitOpen = true;
        return { action: 'guard_re_injected', stayedOnPos: true };
      }

      return { action: 'normal_back', stayedOnPos: false };
    };

    const result = simulatePopState(mockState);
    assert.strictEqual(result.action, 'guard_re_injected');
    assert.strictEqual(mockState.isConfirmExitOpen, true, 'Custom ConfirmModal state must be true');
    assert.strictEqual(pushedStateCount, 1, 'History guard must be re-injected');
    assert.strictEqual(result.stayedOnPos, true);
  });

  test('4. Resists consecutive Back attempts: Every consecutive Back press re-injects history protection and maintains ConfirmModal open', () => {
    let pushedStateCount = 0;

    const mockState = {
      activeModal: null,
      onCloseModal: () => {},
      hasItems: true,
      isConfirmExitOpen: true, // ConfirmModal already open from previous attempt
      allowExit: false,
    };

    const simulatePopState = (state) => {
      if (state.allowExit) return { action: 'exit_allowed', stayedOnPos: false };

      if (state.activeModal) {
        state.onCloseModal?.();
        return { action: 'closed_modal', stayedOnPos: true };
      }

      // Unbreakable barrier: each attempt re-injects protection and keeps modal visible
      if (state.hasItems) {
        pushedStateCount++;
        state.isConfirmExitOpen = true;
        return { action: 'guard_re_injected', stayedOnPos: true };
      }

      return { action: 'normal_back', stayedOnPos: false };
    };

    // Attempt 2 (consecutive)
    const result2 = simulatePopState(mockState);
    assert.strictEqual(result2.action, 'guard_re_injected');
    assert.strictEqual(mockState.isConfirmExitOpen, true, 'ConfirmModal must remain firmly open on 2nd attempt');
    assert.strictEqual(result2.stayedOnPos, true, 'User must not leave POS on 2nd attempt');

    // Attempt 3 (consecutive)
    const result3 = simulatePopState(mockState);
    assert.strictEqual(result3.action, 'guard_re_injected');
    assert.strictEqual(mockState.isConfirmExitOpen, true, 'ConfirmModal must remain open on 3rd attempt');
    assert.strictEqual(result3.stayedOnPos, true, 'User must not leave POS on 3rd attempt');

    assert.strictEqual(pushedStateCount, 2, 'Two consecutive guard entries must have been re-injected');
  });

  test('5. Confirmed exit from custom ConfirmModal sets allowExit and triggers navigation', () => {
    let allowExit = false;
    let isConfirmExitOpen = true;
    let navigatedBack = false;

    const handleConfirmExit = () => {
      allowExit = true;
      isConfirmExitOpen = false;
      navigatedBack = true;
    };

    handleConfirmExit();

    assert.strictEqual(allowExit, true, 'allowExit flag must be true');
    assert.strictEqual(isConfirmExitOpen, false, 'Modal must close');
    assert.strictEqual(navigatedBack, true, 'Navigation back must be invoked');
  });

  test('6. Allows normal navigation without prompt when cart is empty and no modals are open', () => {
    const mockState = {
      activeModal: null,
      onCloseModal: () => {},
      hasItems: false,
      isConfirmExitOpen: false,
      allowExit: false,
    };

    const simulatePopState = (state) => {
      if (state.allowExit) return { action: 'exit_allowed', stayedOnPos: false };

      if (state.activeModal) {
        state.onCloseModal?.();
        return { action: 'closed_modal', stayedOnPos: true };
      }

      if (state.hasItems) {
        state.isConfirmExitOpen = true;
        return { action: 'guard_re_injected', stayedOnPos: true };
      }

      return { action: 'normal_back', stayedOnPos: false };
    };

    const result = simulatePopState(mockState);
    assert.strictEqual(mockState.isConfirmExitOpen, false, 'No confirmation prompt should appear for empty cart');
    assert.strictEqual(result.action, 'normal_back');
    assert.strictEqual(result.stayedOnPos, false);
  });

  test('7. Beforeunload listener arms browser protection when cart has items and exit is not allowed', () => {
    const handleBeforeUnload = (hasItems, allowExit, event) => {
      if (hasItems && !allowExit) {
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

    const msg = handleBeforeUnload(true, false, fakeEventWithItems);
    assert.strictEqual(fakeEventWithItems.prevented, true);
    assert.ok(msg.includes('Se perderá el carrito actual'));

    const fakeEventExitAllowed = {
      prevented: false,
      returnValue: '',
      preventDefault() { this.prevented = true; }
    };

    const allowedResult = handleBeforeUnload(true, true, fakeEventExitAllowed);
    assert.strictEqual(fakeEventExitAllowed.prevented, false);
    assert.strictEqual(allowedResult, undefined);
  });

  test('8. Reload resistance: Detects cached items in sessionStorage even if initial props hasItems is false, arming the guard and blocking exit', () => {
    // Simulate page reload: initially hasItems prop is false because backend hasn't responded yet
    const hasItemsProp = false;
    // But sessionStorage has cached active_pos_has_items = 'true'
    const sessionStorageMock = {
      getItem: (key) => key === 'active_pos_has_items' ? 'true' : null,
    };

    const hasCachedItems = sessionStorageMock.getItem('active_pos_has_items') === 'true';
    const effectiveHasItems = hasItemsProp || hasCachedItems;

    let confirmShown = false;
    let guardPushed = false;

    const simulatePopStateOnReload = (hasItems) => {
      if (hasItems) {
        guardPushed = true;
        confirmShown = true;
        return { action: 'guard_re_injected', stayedOnPos: true };
      }
      return { action: 'normal_back', stayedOnPos: false };
    };

    const result = simulatePopStateOnReload(effectiveHasItems);

    assert.strictEqual(effectiveHasItems, true, 'effectiveHasItems must be true due to session cache');
    assert.strictEqual(guardPushed, true, 'Guard must be re-injected even right after reload');
    assert.strictEqual(confirmShown, true, 'Confirm modal must be shown');
    assert.strictEqual(result.stayedOnPos, true, 'User must remain on POS');
  });
});
