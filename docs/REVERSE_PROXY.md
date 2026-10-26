# Reverse proxy TLS (Caddy) — CommandCenter POS

Caddy termina TLS en **8443** y reenvía a Kestrel en **127.0.0.1:5000**. El backend deja de escuchar en la LAN; los clientes usan `https://<host>:8443/`.

## Quick path

1. Genere el certificado del sitio:
   ```powershell
   pwsh scripts/create-https-cert.ps1 -Subject "CN=<nombre-del-equipo>"
   ```
2. Exporte el PFX a PEM para Caddy:
   ```powershell
   $env:HTTPS_CERT_PASSWORD = "<password-del-pfx>"
   pwsh scripts/export-https-pem.ps1
   ```
3. Genere y valide el Caddyfile:
   ```powershell
   pwsh scripts/setup-caddy-proxy.ps1 -CaddyExe "C:\Tools\caddy\caddy.exe"
   ```
   Agregue `-InstallService` si NSSM está disponible.
4. Configure el backend y reinicie el servicio:
   ```text
   ReverseProxy__Enabled=true
   ReverseProxy__HttpsPort=8443
   Ports__Https=8443
   ```
   En `secrets.json` o como variables de entorno del servicio. El backend queda en `127.0.0.1:5000`.
5. Apunte los clientes a `https://<host>:8443/`.

## Verification

```powershell
curl.exe -k https://localhost:8443/api/health
curl.exe http://127.0.0.1:5000/api/health
```

- El primer comando debe devolver `Healthy`.
- El segundo solo funciona en el servidor (loopback); desde otra máquina debe fallar.
- Abra el POS Web y verifique que SignalR conecta (tasa/autorizaciones) a través del proxy.
- Pruebe pairing remoto: debe seguir exigiendo autenticación y no tratar la conexión como loopback.

## Certificate trust

- **Recomendado:** certificado emitido por una CA interna o pública; navegadores y móviles confían sin instalar nada.
- Si usa el certificado autofirmado del sitio, importe el `.cer` en el almacén raíz de confianza de cada dispositivo (Windows) y como perfil/CA en móviles.
- El desktop mantiene el pin DPAPI por host. Si el certificado es autofirmado y se regenera, hay que actualizar el pin; con un certificado CA-confiable el pin no se bloquea por `SslPolicyErrors.None`.

## Network and security notes

- Caddy debe estar en el mismo host que Kestrel o en un host confiable. Si está en otro host, agregue su IP a `ForwardedHeaders:KnownProxies`.
- No publique el puerto 5000 en el firewall cuando `ReverseProxy:Enabled=true`.
- Caddy reenvía `X-Forwarded-For` y `X-Forwarded-Proto`; el backend los procesa antes de autenticación y rate limiting.
- Si el proxy se expone fuera de la LAN, agregue autenticación de borde, límites de tasa y TLS con un certificado público válido para el nombre DNS.
- Para activar SRE-10 después del rollout, use `Idempotency:RequireCashSessionAndInvoiceStage=true`; consulte `docs/INSTALLATION-advanced.md`.
