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
    if (variantParentProduct) setVariantParentProduct(null);
    else if (isCustomerModalOpen) setIsCustomerModalOpen(false);
    else if (isScannerOpen) setIsScannerOpen(false);
    else if (onCloseExternalModal) onCloseExternalModal();
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
