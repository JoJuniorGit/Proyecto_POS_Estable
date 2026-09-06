import { useState, useCallback } from 'react';

export function usePosModalFlow(isExternalModalOpen = false, onCloseExternalModal = null) {
  const [isCustomerModalOpen, setIsCustomerModalOpen] = useState(false);
  const [isScannerOpen, setIsScannerOpen] = useState(false);
  const [variantParentProduct, setVariantParentProduct] = useState(null);

  const activeModal = isExternalModalOpen
    ? 'external'
    : isCustomerModalOpen
    ? 'customer'
    : isScannerOpen
    ? 'scanner'
    : variantParentProduct
    ? 'variant'
    : null;

  const handleCloseActiveModal = useCallback(() => {
    if (variantParentProduct) {
      setVariantParentProduct(null);
      return true;
    }
    if (isCustomerModalOpen) {
      setIsCustomerModalOpen(false);
      return true;
    }
    if (isScannerOpen) {
      setIsScannerOpen(false);
      return true;
    }
    if (onCloseExternalModal) {
      const res = onCloseExternalModal();
      return res !== undefined ? res : true;
    }
    return false;
  }, [variantParentProduct, isCustomerModalOpen, isScannerOpen, onCloseExternalModal]);

  return {
    activeModal,
    isCustomerModalOpen,
    setIsCustomerModalOpen,
    isScannerOpen,
    setIsScannerOpen,
    variantParentProduct,
    setVariantParentProduct,
    handleCloseActiveModal,
  };
}
