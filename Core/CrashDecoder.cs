using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CrashLens.Core;

public static class CrashDecoder
{
    private static readonly Dictionary<uint, (string Name, string Description)> StopCodes = new()
    {
        [0xA] = ("IRQL_NOT_LESS_OR_EQUAL", "Обращение драйвера или ядра к памяти на недопустимом уровне IRQL. Возможны проблемы с драйвером или оперативной памятью."),
        [0x1E] = ("KMODE_EXCEPTION_NOT_HANDLED", "Исключение в режиме ядра осталось необработанным. Часто связано с драйверами."),
        [0x24] = ("NTFS_FILE_SYSTEM", "Проблема в файловой системе NTFS или связанных драйверах/накопителе."),
        [0x3B] = ("SYSTEM_SERVICE_EXCEPTION", "Исключение при выполнении системной службы. Возможны сбой драйвера или повреждение памяти."),
        [0x50] = ("PAGE_FAULT_IN_NONPAGED_AREA", "Обращение к недоступной памяти. Частые варианты: драйвер, ОЗУ или данные на диске."),
        [0x7E] = ("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", "Системный поток вызвал необработанное исключение, нередко из-за драйвера."),
        [0x7F] = ("UNEXPECTED_KERNEL_MODE_TRAP", "Непредвиденное исключение ядра. Проверьте ОЗУ, драйверы и настройки разгона."),
        [0x9F] = ("DRIVER_POWER_STATE_FAILURE", "Драйвер не смог вовремя завершить операцию смены состояния питания или сна."),
        [0x101] = ("CLOCK_WATCHDOG_TIMEOUT", "Процессор не ответил на ожидаемое прерывание. Возможны аппаратные проблемы, BIOS или разгон."),
        [0x109] = ("CRITICAL_STRUCTURE_CORRUPTION", "Обнаружено повреждение важных структур ядра. Возможны драйверы или нестабильная память."),
        [0x116] = ("VIDEO_TDR_FAILURE", "Видеоадаптер или видеодрайвер не восстановился после зависания."),
        [0x119] = ("VIDEO_SCHEDULER_INTERNAL_ERROR", "Ошибка видеопланировщика. Проверьте видеодрайвер и видеокарту."),
        [0x124] = ("WHEA_UNCORRECTABLE_ERROR", "Windows получила серьёзный аппаратный сигнал об ошибке. Возможны CPU, ОЗУ, питание или иные компоненты."),
        [0x133] = ("DPC_WATCHDOG_VIOLATION", "Операция DPC/ISR выполнялась слишком долго. Нередко указывает на драйвер или устройства хранения."),
        [0x139] = ("KERNEL_SECURITY_CHECK_FAILURE", "Ядро выявило повреждение критической структуры данных; возможны драйверы или ОЗУ."),
        [0x154] = ("UNEXPECTED_STORE_EXCEPTION", "Неожиданная ошибка компонента хранения данных; может быть связана с накопителем/драйвером."),
        [0xC2] = ("BAD_POOL_CALLER", "Недопустимая операция с памятью ядра, часто вследствие ошибки драйвера."),
        [0xC4] = ("DRIVER_VERIFIER_DETECTED_VIOLATION", "Средство Driver Verifier выявило нарушение в работе драйвера."),
        [0xD1] = ("DRIVER_IRQL_NOT_LESS_OR_EQUAL", "Драйвер использовал неправильный адрес памяти на повышенном IRQL."),
        [0xEF] = ("CRITICAL_PROCESS_DIED", "Завершился критически важный процесс Windows. Причину следует уточнять по дампу и сопутствующим событиям."),
        [0xF4] = ("CRITICAL_OBJECT_TERMINATION", "Был завершён критический процесс или поток Windows."),
    };

    private static readonly Dictionary<string, string> ExceptionCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0xC0000005"] = "Нарушение доступа к памяти (Access Violation). Приложение или его модуль попытались обратиться к недопустимому адресу.",
        ["0xC0000409"] = "Аварийное завершение из-за проверки безопасности, например повреждения буфера (Fail Fast).",
        ["0xC000001D"] = "Недопустимая машинная инструкция. Возможна несовместимость процессора, исполняемого файла или повреждение кода.",
        ["0xC0000094"] = "Целочисленное деление на ноль.",
        ["0xE0434352"] = "Необработанное исключение .NET. Для точного диагноза нужны стек вызовов и сведения приложения.",
        ["0xC0000374"] = "Обнаружено повреждение кучи памяти приложения (Heap Corruption)."
    };

    private static string Shorten(string? s, int limit) => string.IsNullOrWhiteSpace(s)
        ? "" : s.Length > limit ? s[..limit] + " …" : s;

    public static CrashEvent? FromRecord(EventRecord record, string channel)
    {
        try
        {
            string xml = record.ToXml();
            XDocument document = XDocument.Parse(xml);
            Dictionary<string, string> data = document.Descendants()
                .Where(x => x.Name.LocalName == "Data")
                .Where(x => x.Attribute("Name") != null)
                .GroupBy(x => (string)x.Attribute("Name")!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);

            int id = record.Id;
            string provider = record.ProviderName ?? "Windows";
            string description;
            try { description = record.FormatDescription() ?? ""; }
            catch (Exception) { description = "Windows не предоставила текст для этого события. Используйте XML и код события."; }

            var item = new CrashEvent
            {
                Key = $"{channel}|{record.RecordId ?? 0}|{record.TimeCreated?.ToUniversalTime().Ticks ?? 0}",
                TimeUtc = record.TimeCreated.HasValue
                    ? new DateTimeOffset(DateTime.SpecifyKind(record.TimeCreated.Value, DateTimeKind.Local)).ToUniversalTime()
                    : DateTimeOffset.UtcNow,
                Channel = channel,
                RecordId = record.RecordId ?? 0,
                EventId = id,
                Provider = provider,
                Severity = record.Level switch { 1 => "Critical", 3 => "Warning", _ => "Error" },
                Category = channel == "Application" ? "Приложение" : "Система",
                Message = Shorten(description, 4000),
                RawXml = Shorten(xml, 15000)
            };

            // Event ID 1001 in System from Windows Error Reporting contains a BugCheck stop code.
            bool isBugcheck = channel == "System" && id == 1001 &&
                (provider.Contains("WER-SystemErrorReporting", StringComparison.OrdinalIgnoreCase) ||
                 provider.Contains("BugCheck", StringComparison.OrdinalIgnoreCase));

            // Kernel-Power 41 can carry a numeric BugcheckCode; zero does NOT prove a BSOD.
            uint? stopCode = null;
            if (channel == "System" && id == 41 && data.TryGetValue("BugcheckCode", out string? rawCode))
            {
                if (uint.TryParse(rawCode, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint n) && n != 0)
                    stopCode = n;
                else if (!string.IsNullOrWhiteSpace(rawCode) && rawCode.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    stopCode = ParseCode(rawCode);
            }
            else if (isBugcheck)
            {
                string? candidate = data.TryGetValue("param1", out string? p) ? p : null;
                Match match = Regex.Match(description, @"0x[\da-fA-F]{1,8}", RegexOptions.IgnoreCase);
                if (match.Success) candidate = match.Value;
                stopCode = ParseCode(candidate);
            }

            if (stopCode is uint bug && bug != 0 && (isBugcheck || id == 41))
            {
                item.Category = "BSOD";
                item.Severity = "Critical";
                item.Code = $"0x{bug:X8}";
                var info = StopCodes.GetValueOrDefault(bug);
                item.Title = string.IsNullOrEmpty(info.Name) ? "Синий экран Windows (BugCheck)" : info.Name;
                item.Explanation = string.IsNullOrEmpty(info.Description)
                    ? "Windows зафиксировала код остановки. По одному коду нельзя надёжно установить первопричину: проверьте драйверы, журнал и дамп памяти."
                    : info.Description + " Это возможные причины, не окончательный диагноз.";
                item.DumpPath = ExtractDumpPath(description);
                return item;
            }

            if (isBugcheck)
            {
                item.Category = "BSOD";
                item.Severity = "Critical";
                item.Title = "Windows перезагрузилась после BugCheck";
                item.Explanation = "Зафиксировано сообщение об остановке Windows, но код не удалось извлечь автоматически. Откройте сведения события и выполните поиск в Google.";
                item.DumpPath = ExtractDumpPath(description);
                return item;
            }

            if (channel == "System" && (id == 41 || id == 6008))
            {
                item.Category = "Перезагрузка";
                item.Severity = "Critical";
                item.Title = id == 41 ? "Неожиданный перезапуск системы" : "Предыдущее завершение Windows было аварийным";
                item.Explanation = "Windows обнаружила некорректное завершение работы. Это бывает при отключении питания, зависании, принудительной перезагрузке или BSOD. Само это событие не доказывает, что был синий экран.";
                return item;
            }

            string? exception = FindExceptionCode(data, description);
            if (!string.IsNullOrEmpty(exception)) item.Code = exception;

            if (channel == "Application" && id == 1000)
            {
                item.Category = "Краш приложения";
                item.Title = "Аварийное завершение приложения";
                item.Explanation = ExceptionCodes.GetValueOrDefault(item.Code)
                    ?? "Приложение завершилось с ошибкой. Для поиска причины проверьте имя сбойного модуля и код исключения в подробностях.";
            }
            else if (channel == "Application" && id == 1002)
            {
                item.Category = "Зависание приложения";
                item.Title = "Приложение перестало отвечать";
                item.Explanation = "Windows зарегистрировала зависание приложения. Проверьте название программы, модуль и сопутствующие события.";
            }
            else if (channel == "Application" && id == 1026)
            {
                item.Category = "Сбой .NET";
                item.Title = "Необработанное исключение .NET";
                item.Explanation = "Программа на .NET аварийно завершилась из-за необработанного исключения. Сообщение события может содержать стек вызовов.";
            }
            else if (channel == "Application" && id == 1001)
            {
                item.Category = "Отчёт Windows";
                item.Title = "Отчёт Windows об ошибке приложения";
                item.Explanation = "Windows Error Reporting зарегистрировал сбой или зависание приложения. Событие может относиться к тому же происшествию, что и запись Event ID 1000.";
            }
            else if (channel == "System" && (id == 7031 || id == 7034))
            {
                item.Category = "Служба Windows";
                item.Title = "Неожиданное завершение службы";
                item.Explanation = "Одна из системных служб аварийно завершилась. Название службы указано в описании события.";
            }
            else if (provider.Contains("WHEA", StringComparison.OrdinalIgnoreCase))
            {
                item.Category = "Оборудование";
                item.Title = "Аппаратное событие WHEA";
                item.Explanation = "Windows Hardware Error Architecture сообщила об аппаратной ошибке или предупреждении. Для причины нужны дополнительные сведения о компоненте.";
            }
            else if (provider.Contains("disk", StringComparison.OrdinalIgnoreCase) || provider.Contains("Ntfs", StringComparison.OrdinalIgnoreCase)
                     || provider.Contains("stor", StringComparison.OrdinalIgnoreCase))
            {
                item.Category = "Накопитель";
                item.Title = "Ошибка устройства или системы хранения";
                item.Explanation = "Событие может касаться диска, контроллера или файловой системы. Проверьте текст события, SMART и состояние подключения.";
            }
            else
            {
                item.Title = item.Severity == "Warning" ? "Предупреждение Windows" : "Ошибка Windows";
                item.Explanation = "Система записала ошибку в журнал. Для диагностики используйте идентификатор события, источник и исходное описание.";
            }
            return item;
        }
        catch (Exception) { return null; }
    }

    private static uint? ParseCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string cleaned = raw.Trim();
        Match codeMatch = Regex.Match(cleaned, @"0x[\da-fA-F]{1,8}", RegexOptions.IgnoreCase);
        if (codeMatch.Success) cleaned = codeMatch.Value;
        if (cleaned.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) cleaned = cleaned[2..];
        return uint.TryParse(cleaned, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint result) && result != 0 ? result : null;
    }

    private static string? FindExceptionCode(Dictionary<string, string> data, string message)
    {
        foreach (string key in new[] { "ExceptionCode", "Exception code" })
        {
            if (data.TryGetValue(key, out string? value) && Regex.IsMatch(value, @"^(?:0x)?[\da-fA-F]{8}$"))
                return "0x" + value.Replace("0x", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
        }
        Match m = Regex.Match(message, @"(?:Exception\s*code|Код\s*исключения)\s*[:：]\s*(0x[\da-fA-F]{8})", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant().Replace("0X", "0x") : null;
    }

    private static string ExtractDumpPath(string message)
    {
        Match m = Regex.Match(message, @"[A-Za-z]:\\[^\r\n<>|]*?\.dmp", RegexOptions.IgnoreCase);
        return m.Success ? m.Value.Trim() : "";
    }
}
