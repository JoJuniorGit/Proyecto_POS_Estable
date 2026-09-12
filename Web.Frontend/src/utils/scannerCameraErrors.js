export function friendlyCameraError(err) {
  if (!err) return 'No se pudo iniciar la cámara.';
  if (err.name === 'NotAllowedError' || err.name === 'PermissionDeniedError') {
    return 'Permiso denegado: autorice el acceso a la cámara en los ajustes del navegador.';
  }
  if (err.name === 'NotFoundError' || err.name === 'DevicesNotFoundError') {
    return 'No se encontró ninguna cámara conectada en este dispositivo.';
  }
  if (err.name === 'NotReadableError' || err.name === 'TrackStartError') {
    return 'La cámara está ocupada por otra aplicación o pestaña del navegador.';
  }
  return `Error de cámara (${err.name || 'Desconocido'}): ${err.message || 'no disponible'}`;
}

export function queryCameraPermission() {
  if (typeof navigator === 'undefined' || typeof navigator.permissions?.query !== 'function') {
    return Promise.resolve('unsupported');
  }
  return navigator.permissions
    .query({ name: 'camera' })
    .then((status) => status.state)
    .catch(() => 'unsupported');
}

export async function resolveCameraGuidance(err) {
  const state = await queryCameraPermission();
  return cameraPermissionGuidance(state, err);
}

export function cameraPermissionGuidance(state, err) {
  const name = err?.name || 'UnknownError';
  if (state === 'denied') {
    return {
      text: 'La cámara está bloqueada en este navegador. Abra el candado en la barra de direcciones y seleccione "Permitir" para la cámara.',
      reloadHint: false,
    };
  }
  if (state === 'prompt') {
    return {
      text: 'Se solicitará el acceso a la cámara: acepte el permiso en el diálogo del navegador. Si el diálogo no aparece, presione Reintentar.',
      reloadHint: false,
    };
  }
  if (state === 'granted' && (name === 'NotAllowedError' || name === 'PermissionDeniedError')) {
    return {
      text: 'El permiso de cámara ya está concedido pero la cámara no arrancó. Presione Reintentar; si el problema persiste, recargue la página.',
      reloadHint: true,
    };
  }
  return { text: friendlyCameraError(err), reloadHint: false };
}

export function shouldFallbackWithoutDeviceId(errorName, hadDeviceId) {
  return Boolean(hadDeviceId && (errorName === 'OverconstrainedError' || errorName === 'NotFoundError' || errorName === 'DevicesNotFoundError'));
}
