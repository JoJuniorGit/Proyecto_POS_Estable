using System;
using System.IO;
using System.Media;

namespace Desktop.Client.Services;

/// <summary>
/// Implementación de retroalimentación auditiva con síntesis de ondas PCM en memoria (WAV de 44.1kHz).
/// Genera tonos de alta fidelidad idénticos a los del cliente web, completamente libres de bloqueos UI.
/// </summary>
public sealed class ScannerFeedbackService : IScannerFeedbackService, IDisposable
{
    private readonly SoundPlayer? _successPlayer;
    private readonly SoundPlayer? _notFoundPlayer;
    private readonly SoundPlayer? _errorPlayer;
    private bool _disposed;

    public ScannerFeedbackService()
    {
        try
        {
            _successPlayer = CreateTonePlayer(frequency: 880, durationMs: 80, amplitude: 0.25f);
            _notFoundPlayer = CreateTonePlayer(frequency: 440, durationMs: 120, amplitude: 0.30f);
            _errorPlayer = CreateTonePlayer(frequency: 220, durationMs: 180, amplitude: 0.35f);
        }
        catch
        {
            // Fallback en caso de que el entorno de audio no permita inicialización directa de streams
        }
    }

    public void PlaySuccess()
    {
        try
        {
            if (_successPlayer != null)
            {
                _successPlayer.Play();
            }
            else
            {
                SystemSounds.Asterisk.Play();
            }
        }
        catch
        {
            try { SystemSounds.Asterisk.Play(); } catch { }
        }
    }

    public void PlayNotFound()
    {
        try
        {
            if (_notFoundPlayer != null)
            {
                _notFoundPlayer.Play();
            }
            else
            {
                SystemSounds.Exclamation.Play();
            }
        }
        catch
        {
            try { SystemSounds.Exclamation.Play(); } catch { }
        }
    }

    public void PlayError()
    {
        try
        {
            if (_errorPlayer != null)
            {
                _errorPlayer.Play();
            }
            else
            {
                SystemSounds.Hand.Play();
            }
        }
        catch
        {
            try { SystemSounds.Hand.Play(); } catch { }
        }
    }

    private static SoundPlayer CreateTonePlayer(double frequency, int durationMs, float amplitude)
    {
        const int sampleRate = 44100;
        int sampleCount = (int)(sampleRate * (durationMs / 1000.0));
        short[] samples = new short[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            double t = (double)i / sampleRate;
            // Curva suave de decaimiento (fade-out exponencial) para eliminar clics acústicos
            double envelope = Math.Exp(-4.0 * t / (durationMs / 1000.0));
            double wave = Math.Sin(2.0 * Math.PI * frequency * t);
            samples[i] = (short)(wave * envelope * amplitude * short.MaxValue);
        }

        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            // Cabecera RIFF WAV 16-bit Mono
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + (sampleCount * 2));
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16); // Subchunk1Size (16 para PCM)
            writer.Write((short)1); // AudioFormat (1 = PCM)
            writer.Write((short)1); // NumChannels (1 = Mono)
            writer.Write(sampleRate); // SampleRate
            writer.Write(sampleRate * 2); // ByteRate (SampleRate * NumChannels * BitsPerSample/8)
            writer.Write((short)2); // BlockAlign (NumChannels * BitsPerSample/8)
            writer.Write((short)16); // BitsPerSample
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(sampleCount * 2); // Subchunk2Size

            for (int i = 0; i < sampleCount; i++)
            {
                writer.Write(samples[i]);
            }
        }

        stream.Position = 0;
        var player = new SoundPlayer(stream);
        player.Load();
        return player;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _successPlayer?.Dispose(); } catch { }
        try { _notFoundPlayer?.Dispose(); } catch { }
        try { _errorPlayer?.Dispose(); } catch { }
    }
}
