using DPUruNet;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace bio_middleware.Services;

public class BioService
{
    public class MiddlewareCaptureResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public string Base64Image { get; set; } = string.Empty;
        public string FmdBase64 { get; set; } = string.Empty;
        public string DiagFmdResultCode { get; set; } = string.Empty;
        public int DiagFidViewCount { get; set; }
    }

    private static readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
    
    // Estado compartido gestionado por BioDiscoveryService
    private static Reader? _activeReader = null;
    private static bool _isReaderOpen = false;
    private static string _statusMessage = "Buscando lector...";
    private static DateTime _lastCheck = DateTime.MinValue;

    /// <summary>
    /// Limpia un string para asegurar que solo contiene caracteres válidos de Base64
    /// </summary>
    private static string CleanBase64(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        
        // 1. Filtrar basura
        string cleaned = new string(input.Where(c => char.IsLetterOrDigit(c) || c == '+' || c == '/' || c == '=').ToArray());
        
        // 2. Base64 solo puede tener padding al final. Si hay "=" en el medio (ej. strings concatenados), cortamos ahí.
        int firstEqual = cleaned.IndexOf('=');
        if (firstEqual != -1)
        {
            int len = firstEqual + 1;
            if (len < cleaned.Length && cleaned[len] == '=') len++; // Permitir '=='
            cleaned = cleaned.Substring(0, len);
        }

        // 3. Limpiar cualquier '=' residual que haya quedado mal formateado al final
        cleaned = cleaned.TrimEnd('=');

        // 4. Asegurar que la longitud sea múltiplo de 4 agregando el padding correcto
        int mod = cleaned.Length % 4;
        if (mod > 0)
        {
            cleaned = cleaned.PadRight(cleaned.Length + (4 - mod), '=');
        }

        return cleaned;
    }

    /// <summary>
    /// Escanea el bus USB buscando lectores. Llamado por el servicio de fondo.
    /// </summary>
    public static void DiscoverReaders()
    {
        // Si hay una captura en curso (lock ocupado), no intentamos Discover
        // para evitar interferir con el bus USB.
        if (_lock.CurrentCount == 0) return;

        try
        {
            ReaderCollection rc = ReaderCollection.GetReaders();
            if (rc.Count > 0)
            {
                if (_activeReader == null || _activeReader.Description.SerialNumber != rc[0].Description.SerialNumber)
                {
                    _activeReader = rc[0];
                    Console.WriteLine($"[Bio] Lector detectado: {_activeReader.Description.Name}");
                }
                _statusMessage = $"Conectado: {_activeReader.Description.Name}";
            }
            else
            {
                if (_activeReader != null)
                {
                    _activeReader.Dispose();
                    _activeReader = null;
                    _isReaderOpen = false;
                    Console.WriteLine("[Bio] Lector desconectado.");
                }
                _statusMessage = "No conectado";
            }
            _lastCheck = DateTime.Now;
        }
        catch (Exception ex)
        {
            _statusMessage = $"Error de hardware: {ex.Message}";
            _activeReader = null;
            Console.WriteLine($"[Discovery] Error escaneando: {ex.Message}");
        }
    }

    public static async Task<MiddlewareCaptureResult> CaptureFingerprintAsync(CancellationToken ct, int timeoutMs = 15000)
    {
        // Aseguramos que solo una petición use el lector a la vez
        if (!await _lock.WaitAsync(0)) 
        {
            return new MiddlewareCaptureResult { Success = false, Message = "El lector ya está siendo usado por otra petición." };
        }

        try
        {
            // Verificamos si tenemos un lector listo (del discovery de fondo)
            if (_activeReader == null)
            {
                // Un último intento rápido por si el discovery aún no corrió
                DiscoverReaders();
                if (_activeReader == null)
                    return new MiddlewareCaptureResult { Success = false, Message = "Lector no encontrado." };
            }

            var reader = _activeReader;
            
            // Optimización: Solo abrir si no está ya abierto
            if (!_isReaderOpen)
            {
                var openRes = reader.Open(Constants.CapturePriority.DP_PRIORITY_EXCLUSIVE);
                if (openRes == Constants.ResultCode.DP_DEVICE_BUSY)
                {
                    return new MiddlewareCaptureResult { Success = false, Message = "Lector ocupado (DP_BUSY)." };
                }
                if (openRes != Constants.ResultCode.DP_SUCCESS)
                {
                    return new MiddlewareCaptureResult { Success = false, Message = $"Error al abrir: {openRes}" };
                }
                _isReaderOpen = true;
                Console.WriteLine("[Bio] Sesión con el lector establecida (Abierto).");
            }

            try 
            {
                Console.WriteLine($"[Bio] Capturando... (Dedo en sensor)");

                var captureTask = Task.Run(() => 
                {
                    return reader.Capture(
                        Constants.Formats.Fid.ANSI, 
                        Constants.CaptureProcessing.DP_IMG_PROC_DEFAULT,
                        timeoutMs,
                        500
                    );
                });

                // Esperamos la captura o la cancelación del usuario (HTTP Aborted)
                var completedTask = await Task.WhenAny(captureTask, Task.Delay(timeoutMs + 1000, ct));

                if (ct.IsCancellationRequested)
                {
                    reader.CancelCapture();
                    Console.WriteLine("[Bio] Petición abortada por el usuario.");
                    throw new OperationCanceledException(ct);
                }

                if (completedTask != captureTask)
                {
                    reader.CancelCapture();
                    return new MiddlewareCaptureResult { Success = false, Message = "Tiempo de espera agotado." };
                }

                var captureResult = await captureTask;

                if (captureResult.ResultCode != Constants.ResultCode.DP_SUCCESS)
                {
                    _isReaderOpen = false; // Forzar re-apertura si hubo error de hardware
                    return new MiddlewareCaptureResult { Success = false, Message = $"Error hardware: {captureResult.ResultCode}" };
                }

                var fid = captureResult.Data;
                if (fid == null || fid.Views == null || fid.Views.Count == 0)
                {
                    return new MiddlewareCaptureResult { Success = false, Message = "Captura sin imagen." };
                }

                var view = fid.Views[0];
                var fmdPreRegResult = FeatureExtraction.CreateFmdFromFid(fid, Constants.Formats.Fmd.DP_PRE_REGISTRATION);
                var fmdAnsiResult = FeatureExtraction.CreateFmdFromFid(fid, Constants.Formats.Fmd.ANSI);

                if (fmdPreRegResult.ResultCode != Constants.ResultCode.DP_SUCCESS || fmdPreRegResult.Data == null ||
                    fmdAnsiResult.ResultCode != Constants.ResultCode.DP_SUCCESS || fmdAnsiResult.Data == null)
                {
                    return new MiddlewareCaptureResult { Success = false, Message = $"Error extrayendo minutiae." };
                }

                int minutiaeCount = 0;
                if (fmdPreRegResult.Data.Views != null && fmdPreRegResult.Data.Views.Count > 0)
                {
                    minutiaeCount = fmdPreRegResult.Data.Views[0].MinutiaeCount;
                }

                string combinedFmd = $"{Convert.ToBase64String(fmdPreRegResult.Data.Bytes)}|{Convert.ToBase64String(fmdAnsiResult.Data.Bytes)}";

                return new MiddlewareCaptureResult
                {
                    Success = true,
                    Message = $"Dedo capturado (Minucias: {minutiaeCount}, Formato: Dual).",
                    Base64Image = CreateBitmapFromView(view),
                    FmdBase64 = combinedFmd,
                    DiagFidViewCount = fid.Views.Count
                };
            }
            finally
            {
                // No llamamos a dispose ni close. CancelCapture es suficiente.
                // Mantener _activeReader vivo permite que se reuse en la siguiente petición.
            }

        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.WriteLine($"[Error] {ex.Message}\n{ex.StackTrace}");
            return new MiddlewareCaptureResult { Success = false, Message = $"Excepción: {ex.Message} | {ex.StackTrace?.Split('\n')[0]}" };
        }
        finally
        {
            _lock.Release();
        }
    }

    public class MiddlewareVerifyResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public bool Match { get; set; }
        public int Score { get; set; }
        public int MatchedIndex { get; set; }
        public string Base64Image { get; set; } = string.Empty;
        public string FmdBase64 { get; set; } = string.Empty;
    }

    public static async Task<MiddlewareVerifyResult> VerifyFingerprintAsync(List<string> candidateFmdsBase64, CancellationToken ct, int timeoutMs = 15000)
    {
        // Reutilizamos la lógica de captura para obtener el FMD del dedo en el lector
        var captureResult = await CaptureFingerprintAsync(ct, timeoutMs);
        
        if (!captureResult.Success || string.IsNullOrEmpty(captureResult.FmdBase64))
        {
            return new MiddlewareVerifyResult 
            { 
                Success = false, 
                Message = captureResult.Message, 
                Match = false 
            };
        }

        try
        {
            // La huella capturada es dual: pre_reg|ansi
            string[] probeParts = captureResult.FmdBase64.Split('|');
            string probeAnsiB64 = probeParts.Length > 1 ? probeParts[1] : probeParts[0];

            probeAnsiB64 = CleanBase64(probeAnsiB64);

            byte[] probeBytes = Convert.FromBase64String(probeAnsiB64);
            var probeFmdResult = Importer.ImportFmd(probeBytes, Constants.Formats.Fmd.ANSI, Constants.Formats.Fmd.ANSI);
            
            if (probeFmdResult.ResultCode != Constants.ResultCode.DP_SUCCESS || probeFmdResult.Data == null)
            {
                return new MiddlewareVerifyResult { Success = false, Message = $"Error importando huella capturada: {probeFmdResult.ResultCode}", Match = false };
            }
            
            Fmd probeFmd = probeFmdResult.Data;
            int thresholdScore = (0x7FFFFFFF / 100000); // PROBABILITY_ONE / 100000 -> False Accept Rate de 0.001%
            int lowestScore = int.MaxValue;
            string compareError = "";
            
            for (int i = 0; i < candidateFmdsBase64.Count; i++)
            {
                string candidateBase64 = candidateFmdsBase64[i];
                if (string.IsNullOrEmpty(candidateBase64)) continue;

                string[] candParts = candidateBase64.Split('|');
                string candAnsiB64 = candParts.Length > 1 ? candParts[1] : candParts[0];

                // Limpieza absoluta de caracteres extraños (ej. \n, \r, comillas, llaves)
                candAnsiB64 = CleanBase64(candAnsiB64);

                byte[] candidateBytes = Convert.FromBase64String(candAnsiB64);
                
                // Usamos DP_REGISTRATION (como se guarda en DB) y ANSI como fallback
                Constants.Formats.Fmd[] formatsToTry = new[] { 
                    Constants.Formats.Fmd.DP_REGISTRATION, 
                    Constants.Formats.Fmd.ANSI 
                };

                bool comparedSuccessfully = false;

                foreach (var format in formatsToTry)
                {
                    var candidateFmdResult = Importer.ImportFmd(candidateBytes, format, format);
                    if (candidateFmdResult.ResultCode == Constants.ResultCode.DP_SUCCESS && candidateFmdResult.Data != null)
                    {
                        Fmd candidateFmd = candidateFmdResult.Data;
                        CompareResult compareResult = Comparison.Compare(probeFmd, 0, candidateFmd, 0);

                        if (compareResult.ResultCode == Constants.ResultCode.DP_SUCCESS)
                        {
                            comparedSuccessfully = true;
                            var candidateMinutiae = (candidateFmd.Views != null && candidateFmd.Views.Count > 0) ? candidateFmd.Views[0].MinutiaeCount : 0;
                            var probeMinutiae = (probeFmd.Views != null && probeFmd.Views.Count > 0) ? probeFmd.Views[0].MinutiaeCount : 0;

                            if (compareResult.Score < lowestScore)
                            {
                                lowestScore = compareResult.Score;
                            }
                            if (compareResult.Score < thresholdScore)
                            {
                                return new MiddlewareVerifyResult 
                                { 
                                    Success = true, 
                                    Message = "Huella verificada con éxito.", 
                                    Match = true,
                                    Score = compareResult.Score,
                                    MatchedIndex = i,
                                    Base64Image = captureResult.Base64Image,
                                    FmdBase64 = captureResult.FmdBase64
                                };
                            }
                            break; // Si la comparación fue exitosa en este formato (aunque no sea match por score), pasamos al siguiente candidato
                        }
                        else
                        {
                            compareError = $"Format {format}: {compareResult.ResultCode}";
                        }
                    }
                }
            }

            if (lowestScore == int.MaxValue)
            {
                return new MiddlewareVerifyResult 
                { 
                    Success = false, 
                    Message = "Huella capturada no coincide con las registradas.", 
                    Match = false,
                    Base64Image = captureResult.Base64Image,
                    FmdBase64 = captureResult.FmdBase64
                };
            }

            return new MiddlewareVerifyResult 
            { 
                Success = true, 
                Message = "Huella distinta detectada.", 
                Match = false,
                Base64Image = captureResult.Base64Image,
                FmdBase64 = captureResult.FmdBase64
            };
        }
        catch (Exception ex)
        {
            return new MiddlewareVerifyResult { Success = false, Message = $"Error de validación: {ex.Message}", Match = false };
        }
    }

    public static async Task<MiddlewareCaptureResult> EnrollFingerprintAsync(List<string> preEnrollmentFmdsBase64, CancellationToken ct)
    {
        try
        {
            List<Fmd> fmds = new List<Fmd>();
            var logPath = "C:\\sc_new\\bio_middleware_log.txt";
            System.IO.File.WriteAllText(logPath, $"=== SESION DE ENROLAMIENTO {DateTime.Now} ===\r\n");
            
            System.IO.File.AppendAllText(logPath, $"Iniciando fusión de huellas con {preEnrollmentFmdsBase64.Count} muestras...\r\n");
            
            for (int i = 0; i < preEnrollmentFmdsBase64.Count; i++)
            {
                string b64 = preEnrollmentFmdsBase64[i];
                if (string.IsNullOrEmpty(b64)) continue;

                string[] parts = b64.Split('|');
                string preRegB64 = parts[0];

                // Limpieza de caracteres extraños
                preRegB64 = CleanBase64(preRegB64);

                byte[] bytes = Convert.FromBase64String(preRegB64);
                var importResult = Importer.ImportFmd(bytes, Constants.Formats.Fmd.DP_PRE_REGISTRATION, Constants.Formats.Fmd.DP_PRE_REGISTRATION);
                
                if (importResult.ResultCode != Constants.ResultCode.DP_SUCCESS || importResult.Data == null)
                {
                    System.IO.File.AppendAllText(logPath, $"Huella {i} inválida al importar: {importResult.ResultCode}\r\n");
                    return new MiddlewareCaptureResult { Success = false, Message = $"Uno de los FMDs proporcionados es inválido ({importResult.ResultCode})." };
                }

                Fmd fmd = importResult.Data;
                int minutiaeCount = (fmd.Views != null && fmd.Views.Count > 0) ? fmd.Views[0].MinutiaeCount : 0;
                string prefix = b64.Length > 20 ? b64.Substring(0, 20) : b64;
                System.IO.File.AppendAllText(logPath, $"Huella {i}: Longitud={b64.Length}, Minucias={minutiaeCount}, Prefijo={prefix}\r\n");

                fmds.Add(fmd);
            }

            IEnumerable<Fmd> fmdsEnumerable = fmds;
            var enrollResult = Enrollment.CreateEnrollmentFmd(Constants.Formats.Fmd.DP_REGISTRATION, fmdsEnumerable);
            System.IO.File.AppendAllText(logPath, $"Resultado de la fusión: {enrollResult.ResultCode}\r\n");
            
            if (enrollResult.ResultCode == Constants.ResultCode.DP_SUCCESS)
            {
                System.IO.File.AppendAllText(logPath, "Fusión Exitosa!\r\n");
                return new MiddlewareCaptureResult 
                { 
                    Success = true, 
                    Message = "Plantilla de enrolamiento generada exitosamente.", 
                    FmdBase64 = Convert.ToBase64String(enrollResult.Data.Bytes)
                };
            }
            else if (enrollResult.ResultCode == Constants.ResultCode.DP_ENROLLMENT_INVALID_SET)
            {
                System.IO.File.AppendAllText(logPath, "Error: DP_ENROLLMENT_INVALID_SET (No corresponden al mismo dedo)\r\n");
                return new MiddlewareCaptureResult { Success = false, Message = "Las capturas no corresponden al mismo dedo o no tienen calidad suficiente." };
            }
            else 
            {
                System.IO.File.AppendAllText(logPath, $"Error: {enrollResult.ResultCode}\r\n");
                return new MiddlewareCaptureResult { Success = false, Message = $"Error fusionando huellas: {enrollResult.ResultCode}" };
            }
        }
        catch (Exception ex)
        {
            return new MiddlewareCaptureResult { Success = false, Message = $"Excepción enrolando: {ex.Message}" };
        }
    }

    public static string GetStatus()
    {
        // RESPUESTA INSTANTÁNEA DESDE MEMORIA
        return _statusMessage;
    }

    private static string CreateBitmapFromView(Fid.Fiv view)
    {
        try
        {
            using var bmp = new Bitmap(view.Width, view.Height, PixelFormat.Format8bppIndexed);
            var pal = bmp.Palette;
            for (int i = 0; i <= 255; i++) pal.Entries[i] = Color.FromArgb(i, i, i);
            bmp.Palette = pal;
            bmp.SetResolution(500, 500);

            var bmpData = bmp.LockBits(new Rectangle(0, 0, view.Width, view.Height), ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);
            Marshal.Copy(view.Bytes, 0, bmpData.Scan0, view.Bytes.Length);
            bmp.UnlockBits(bmpData);

            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return Convert.ToBase64String(ms.ToArray());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BioService] Error creando Bitmap: {ex.Message}");
            return "";
        }
    }
}
