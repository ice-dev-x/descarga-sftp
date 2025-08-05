using System;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Renci.SshNet;

class Program
{
    static void Main()
    {
        Console.WriteLine("Directorio actual: " + Directory.GetCurrentDirectory());

        IConfiguration config;
        try
        {
            config = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false)
                .Build();

            Console.WriteLine("Contenido de appsettings.json:");
            Console.WriteLine(File.ReadAllText("appsettings.json"));
        }
        catch (Exception e)
        {
            Console.WriteLine("Error cargando appsettings.json: " + e.Message);
            EsperarSalida();
            return;
        }

        string host = config["Sftp:Host"] ?? "localhost";
        int port = int.Parse(config["Sftp:Port"] ?? "22");
        string user = config["Sftp:Username"] ?? throw new Exception("Falta Username en config");
        string pass = config["Sftp:Password"] ?? throw new Exception("Falta Password en config");
        string remoteBase = config["Sftp:RemoteBasePath"] ?? "/";
        string localBase = config["Sftp:LocalDownloadPath"] ?? @"C:\DatosFTP\Descargas";

        if (!TryEnsureDirectory(localBase))
        {
            string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DescargasFTP");
            Console.WriteLine($"No se pudo crear '{localBase}', usando fallback '{fallback}'");
            localBase = fallback;
            if (!TryEnsureDirectory(localBase))
            {
                Console.WriteLine($"Fallo también creando fallback '{localBase}'. Salida.");
                EsperarSalida();
                return;
            }
        }

        string logPath = Path.Combine(localBase, $"descarga_log_{DateTime.Now:yyyyMMdd}.txt");

        void Log(string msg)
        {
            Console.WriteLine(msg);
            try
            {
                File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}{Environment.NewLine}");
            }
            catch { }
        }

        try
        {
            using var sftp = new SftpClient(host, port, user, pass);
            Console.WriteLine("Intentando conectar...");
            sftp.Connect();
            Log("Conectado a SFTP.");

            if (!sftp.Exists(remoteBase))
            {
                Log($"Ruta remota inválida: {remoteBase}");
                EsperarSalida();
                return;
            }

            sftp.ChangeDirectory(remoteBase);
            var files = sftp.ListDirectory(remoteBase)
                            .Where(f => !f.Name.StartsWith(".") && !f.IsDirectory)
                            .ToList();

            if (files.Count == 0)
            {
                Log("No se encontraron archivos para descargar.");
            }

            foreach (var file in files)
            {
                string localPath = Path.Combine(localBase, file.Name);

                if (File.Exists(localPath))
                {
                    Log($"Ya existe en base, intentando organizar si no se hizo antes: {file.Name}");
                    OrganizarArchivo(file.Name, localPath, localBase, Log);
                    continue;
                }

                try
                {
                    using (var fs = File.OpenWrite(localPath))
                    {
                        sftp.DownloadFile(file.FullName, fs);
                    }
                    Log($"Descargado: {file.Name}");
                    OrganizarArchivo(file.Name, localPath, localBase, Log);
                }
                catch (Exception exFile)
                {
                    Log($"Error descargando {file.Name}: {exFile.Message}");
                }
            }

            sftp.Disconnect();
            Log("Desconectado de SFTP.");
        }
        catch (Exception ex)
        {
            Log($"Error general: {ex.Message}");
        }

        EsperarSalida();
    }

static void OrganizarArchivo(string fileName, string currentFullPath, string baseDownloadPath, Action<string> Log)
{
    var (fecha, _) = ExtractDateAndType(fileName); // ignoramos esSemanal
    string destino;

    if (fecha.HasValue)
    {
        var cultura = new CultureInfo("es-ES");

        // Carpeta del mes: "07 Julio"
        string mesNumero = fecha.Value.ToString("MM");
        string nombreMes = cultura.DateTimeFormat.GetMonthName(fecha.Value.Month);
        nombreMes = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(nombreMes); // Capitaliza
        string carpetaMes = $"{mesNumero} {nombreMes}";

        // Carpeta del día: "31Jul2025"
        string dia = fecha.Value.ToString("dd");
        string mesAbreviado = cultura.DateTimeFormat.GetAbbreviatedMonthName(fecha.Value.Month);
        mesAbreviado = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(mesAbreviado.Replace(".", "")); // Capitaliza
        string carpetaDia = $"{dia}{mesAbreviado}{fecha.Value.Year}";

        destino = Path.Combine(baseDownloadPath,
                fecha.Value.ToString("yyyy"),
                carpetaMes,
                carpetaDia);
    }
    else
    {
        destino = Path.Combine(baseDownloadPath, "sin_fecha");
    }

    try
    {
        Directory.CreateDirectory(destino);
        string nuevoPath = Path.Combine(destino, fileName);

        if (File.Exists(nuevoPath))
        {
            Log($"El archivo ya existe en destino final, no se sobrescribe: {nuevoPath}");
            return;
        }

        File.Move(currentFullPath, nuevoPath);
        Log($"Archivo organizado en: {nuevoPath}");
    }
    catch (Exception ex)
    {
        Log($"Error organizando {fileName}: {ex.Message}");
    }
}


    static (DateTime? fecha, bool esSemanal) ExtractDateAndType(string fileName)
{
    // 1. Rango semanal en español: "del 26 al 31 de julio 2025"
    var rangoRegex = new Regex(@"del\s+(\d{1,2})\s+al\s+(\d{1,2})\s+de\s+([A-Za-záéíóúñÑ]+)\s+(\d{4})", RegexOptions.IgnoreCase);
    var m = rangoRegex.Match(fileName);
    if (m.Success)
    {
        int dayFinal = int.Parse(m.Groups[2].Value);
        string monthName = m.Groups[3].Value.ToLowerInvariant();
        int year = int.Parse(m.Groups[4].Value);
        int month = MonthNameToNumber(monthName);
        if (month != 0)
            return (new DateTime(year, month, dayFinal), true);
    }

    // 2. Caso especial para archivos tipo "Malla_CHB_08052025"
    if (fileName.Contains("Malla_CHB_"))
    {
        var mallaRegex = new Regex(@"Malla_CHB_(\d{8})");
        m = mallaRegex.Match(fileName);
        if (m.Success)
        {
            string fechaStr = m.Groups[1].Value; // "08052025"
            // Formato MMddyyyy
            int month = int.Parse(fechaStr.Substring(0, 2));
            int day = int.Parse(fechaStr.Substring(2, 2));
            int year = int.Parse(fechaStr.Substring(4, 4));

            if (IsValidDate(year, month, day))
                return (new DateTime(year, month, day), false);
        }
    }

    // 3. Formato como "31Jul2025", "04Ago2025", etc.
    var diaMesAnoRegex = new Regex(@"(\d{1,2})([A-Za-z]{3,})(\d{4})", RegexOptions.IgnoreCase);
    m = diaMesAnoRegex.Match(fileName);
    if (m.Success)
    {
        int day = int.Parse(m.Groups[1].Value);
        string monthPart = m.Groups[2].Value.ToLowerInvariant();
        int year = int.Parse(m.Groups[3].Value);

        int month = MonthNameToNumber(monthPart);
        if (month != 0 && IsValidDate(year, month, day))
            return (new DateTime(year, month, day), false);
    }

    // 4. Compacto numérico tipo "07312025" o "31072025"
    var compactRegex = new Regex(@"(\d{2})(\d{2})(\d{4})");
    m = compactRegex.Match(fileName);
    if (m.Success)
    {
        int p1 = int.Parse(m.Groups[1].Value);
        int p2 = int.Parse(m.Groups[2].Value);
        int year = int.Parse(m.Groups[3].Value);

        if (p2 > 12)
        {
            int month = p1;
            int day = p2;
            if (IsValidDate(year, month, day))
                return (new DateTime(year, month, day), false);
        }
        else
        {
            int day = p1;
            int month = p2;
            if (IsValidDate(year, month, day))
                return (new DateTime(year, month, day), false);
        }
    }

    return (null, false);
}


    static bool IsValidDate(int year, int month, int day)
    {
        if (month < 1 || month > 12) return false;
        if (day < 1) return false;
        return day <= DateTime.DaysInMonth(year, month);
    }

    static int MonthNameToNumber(string name)
    {
        name = name.ToLowerInvariant();
        return name switch
        {
            "enero" or "ene" => 1,
            "febrero" or "feb" => 2,
            "marzo" or "mar" => 3,
            "abril" or "abr" => 4,
            "mayo" => 5,
            "junio" or "jun" => 6,
            "julio" or "jul" => 7,
            "agosto" or "ago" => 8,
            "septiembre" or "sep" or "set" => 9,
            "octubre" or "oct" => 10,
            "noviembre" or "nov" => 11,
            "diciembre" or "dic" => 12,

            // Inglés
            "january" or "jan" => 1,
            "february" or "feb" => 2,
            "march" or "mar" => 3,
            "april" or "apr" => 4,
            "may" => 5,
            "june" or "jun" => 6,
            "july" or "jul" => 7,
            "august" or "aug" => 8,
            "september" or "sep" => 9,
            "october" or "oct" => 10,
            "november" or "nov" => 11,
            "december" or "dec" => 12,
            _ => 0,
        };
    }

    static bool TryEnsureDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            return true;
        }
        catch (Exception e)
        {
            Console.WriteLine($"No se pudo crear directorio '{path}': {e.Message}");
            return false;
        }
    }

    static void EsperarSalida()
    {
        Console.WriteLine("Presione ENTER para salir...");
        Console.ReadLine();
    }
}
