export function shouldRecoverClosedShift(message, status) {
  const normalizedMessage = typeof message === 'string' ? message.toLowerCase() : '';
  return normalizedMessage.includes('cerrado') || status === 400 || status === 409;
}
