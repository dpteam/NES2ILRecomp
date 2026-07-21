// Program.cs
// NES Static Recompiler / Lifter to .NET Assembly
// Один файл. .NET Framework 2.0..4.8. Без сторонних библиотек.

using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using System.Drawing;
using System.CodeDom.Compiler;
using Microsoft.CSharp;
using System.Xml.Serialization;
using System.Globalization;

namespace NesLifter.Studio
{
    static class Program
    {
        static Options _options;
        static StateManager _state;
        static TrayManager _tray;

        [STAThread]
        static int Main(string[] args)
        {
            Options opts = Options.Parse(args);
            if (opts.ShowHelp)
            {
                HelpPrinter.Print();
                return 0;
            }

            _options = opts;
            SetupLogging(opts);

            foreach (string u in opts.UnknownArgs)
                Log.Warn("Неизвестный аргумент: " + u);

            AppDomain.CurrentDomain.UnhandledException += delegate (object sender, UnhandledExceptionEventArgs e)
            {
                Log.Error("Unhandled exception: " + e.ExceptionObject);
                if (_state != null) _state.Save();
            };

            Console.CancelKeyPress += delegate (object sender, ConsoleCancelEventArgs e)
            {
                Log.Warn("Получен Ctrl+C/Ctrl+Break. Сохраняю состояние...");
                if (_state != null) _state.Save();
                e.Cancel = false;
            };

            Status.Changed += delegate (string s)
            {
                if (_tray != null) _tray.SetStatus(s);
            };

            Log.Info("NES Static Recompiler запущен.");
            Log.Info("Выходной каталог: " + opts.OutputPath);

            _state = new StateManager(opts);
            _state.Load();
            _state.StartTimer();

            if (!opts.NoTray && Environment.UserInteractive)
            {
                _tray = new TrayManager();
                _tray.Start();
            }

            int exitCode = 0;
            try
            {
                if (string.IsNullOrEmpty(opts.InputPath))
                {
                    if (CanInteractive())
                    {
                        if (!Interactive(opts))
                            return 0;
                    }
                    else
                    {
                        HelpPrinter.Print();
                        return 1;
                    }
                }

                Pipeline pipeline = new Pipeline(opts, _state);
                exitCode = pipeline.Run();
            }
            catch (Exception ex)
            {
                Log.Error("Фатальная ошибка: " + ex.Message);
                Log.Debug(ex.ToString());
                exitCode = 1;
            }
            finally
            {
                if (_state != null)
                {
                    _state.StopTimer();
                    _state.Save();
                }

                if (_tray != null)
                {
                    if (opts.WaitAfter)
                    {
                        Log.Info("Работа завершена. Приложение осталось в трее. Выход через меню трея.");
                        _tray.WaitForExit();
                    }
                    else
                    {
                        _tray.Shutdown();
                    }
                }
                else if (opts.WaitAfter)
                {
                    Console.WriteLine("Нажмите любую клавишу для выхода...");
                    try { Console.ReadKey(true); } catch { }
                }
            }

            return exitCode;
        }

        static bool CanInteractive()
        {
            return Environment.UserInteractive && !Console.IsInputRedirected && !Console.IsOutputRedirected;
        }

        static bool Interactive(Options opts)
        {
            string[] items = new string[4];
            items[0] = "Указать путь к ROM или папке (сейчас: <нет>)";
            items[1] = "Рекурсивная обработка папок: выкл";
            items[2] = "Запустить рекомпиляцию";
            items[3] = "Выход";

            while (true)
            {
                int sel = ConsoleUI.Select("NES Static Recompiler -- TUI", items);
                if (sel < 0)
                {
                    HelpPrinter.Print();
                    return false;
                }

                if (sel == 0)
                {
                    string p = ConsoleUI.AskPath();
                    if (!string.IsNullOrEmpty(p))
                    {
                        opts.InputPath = p;
                        items[0] = "Указать путь (сейчас: " + p + ")";
                    }
                }
                else if (sel == 1)
                {
                    opts.Recursive = !opts.Recursive;
                    items[1] = "Рекурсивная обработка папок: " + (opts.Recursive ? "вкл" : "выкл");
                }
                else if (sel == 2)
                {
                    if (!string.IsNullOrEmpty(opts.InputPath))
                        return true;

                    Log.Warn("Сначала укажите путь к ROM или папке.");
                }
                else if (sel == 3)
                {
                    return false;
                }
            }
        }

        static void SetupLogging(Options opts)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            string logFile = null;
            try
            {
                string logDir = Path.Combine(Environment.CurrentDirectory, "logs");
                Directory.CreateDirectory(logDir);
                string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                logFile = Path.Combine(logDir, "NesLifter_" + ts + ".log");
            }
            catch
            {
                logFile = null;
            }

            Trace.Listeners.Clear();
            Trace.Listeners.Add(new ColorConsoleFileTraceListener(logFile));
            Trace.AutoFlush = true;

            if (logFile == null)
                Log.Warn("Не удалось создать файл лога. Лог будет только в консоли.");
            else
                Log.Info("Файл лога: " + logFile);
        }
    }

    public static class Log
    {
        public static void Info(string message) { Trace.WriteLine(message, "INFO"); }
        public static void Step(string message) { Trace.WriteLine(message, "STEP"); }
        public static void Ok(string message) { Trace.WriteLine(message, "OK"); }
        public static void Warn(string message) { Trace.WriteLine(message, "WARN"); }
        public static void Error(string message) { Trace.WriteLine(message, "ERROR"); }
        public static void Debug(string message) { Trace.WriteLine(message, "DEBUG"); }
    }

    public static class Status
    {
        public static event Action<string> Changed;

        public static void Set(string status)
        {
            try { Console.Title = "NES Lifter - " + status; } catch { }
            Action<string> h = Changed;
            if (h != null) h(status);
        }
    }

    public class ColorConsoleFileTraceListener : TraceListener
    {
        StreamWriter _writer;
        object _lock = new object();

        public ColorConsoleFileTraceListener(string fileName)
        {
            if (!string.IsNullOrEmpty(fileName))
            {
                try
                {
                    _writer = new StreamWriter(fileName, true, Encoding.UTF8);
                    _writer.AutoFlush = true;
                }
                catch
                {
                    _writer = null;
                }
            }
        }

        public override void Write(string message)
        {
            WriteRaw(message, false, "INFO");
        }

        public override void WriteLine(string message)
        {
            WriteRaw(message, true, "INFO");
        }

        public override void TraceEvent(TraceEventCache eventCache, string source, TraceEventType eventType, int id, string message)
        {
            WriteFormatted(message, source, eventType);
        }

        public override void TraceEvent(TraceEventCache eventCache, string source, TraceEventType eventType, int id, string format, params object[] args)
        {
            string msg;
            if (format == null) msg = string.Empty;
            else if (args == null || args.Length == 0) msg = format;
            else msg = string.Format(format, args);
            WriteFormatted(msg, source, eventType);
        }

        public override void Fail(string message)
        {
            WriteFormatted(message, "ERROR", TraceEventType.Error);
        }

        public override void Fail(string message, string detailMessage)
        {
            WriteFormatted(message + " " + detailMessage, "ERROR", TraceEventType.Error);
        }

        void WriteFormatted(string message, string category, TraceEventType type)
        {
            string cat = string.IsNullOrEmpty(category) ? type.ToString() : category;
            string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            string full = "[" + time + "] [" + cat + "] " + (message ?? string.Empty);

            lock (_lock)
            {
                WriteConsole(full, cat, true);
                if (_writer != null)
                {
                    try { _writer.WriteLine(full); } catch { }
                }
            }
        }

        void WriteRaw(string message, bool line, string category)
        {
            lock (_lock)
            {
                WriteConsole(message ?? string.Empty, category, line);
                if (_writer != null)
                {
                    try
                    {
                        if (line) _writer.WriteLine(message);
                        else _writer.Write(message);
                    }
                    catch { }
                }
            }
        }

        void WriteConsole(string text, string category, bool line)
        {
            ConsoleColor old = ConsoleColor.Gray;
            bool restore = false;
            try
            {
                old = Console.ForegroundColor;
                Console.ForegroundColor = GetColor(category);
                restore = true;
            }
            catch { }

            try
            {
                if (line) Console.WriteLine(text);
                else Console.Write(text);
            }
            finally
            {
                if (restore)
                {
                    try { Console.ForegroundColor = old; } catch { }
                }
            }
        }

        ConsoleColor GetColor(string category)
        {
            if (string.IsNullOrEmpty(category)) return ConsoleColor.Gray;
            switch (category.ToUpperInvariant())
            {
                case "ERROR": return ConsoleColor.Red;
                case "WARN": return ConsoleColor.Yellow;
                case "OK": return ConsoleColor.Green;
                case "STEP": return ConsoleColor.Cyan;
                case "DEBUG": return ConsoleColor.DarkGray;
                case "PROGRESS": return ConsoleColor.Magenta;
                default: return ConsoleColor.Gray;
            }
        }

        public override void Close()
        {
            lock (_lock)
            {
                if (_writer != null)
                {
                    try { _writer.Close(); } catch { }
                    _writer = null;
                }
            }
            base.Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Close();
            base.Dispose(disposing);
        }
    }

    public class Options
    {
        public string InputPath;
        public string OutputPath;
        public bool Recursive;
        public bool NoTray;
        public bool WaitAfter;
        public bool Fresh;
        public bool NoCompile;
        public bool SaveSource = true;
        public int CheckpointMinutes = 10;
        public bool ShowHelp;
        public List<string> UnknownArgs = new List<string>();

        public static Options Parse(string[] args)
        {
            Options o = new Options();

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];

                if (Name(a, "--help", "-h", "/?"))
                {
                    o.ShowHelp = true;
                }
                else if (Name(a, "-r", "--recursive"))
                {
                    o.Recursive = true;
                }
                else if (Name(a, "--no-tray"))
                {
                    o.NoTray = true;
                }
                else if (Name(a, "--wait"))
                {
                    o.WaitAfter = true;
                }
                else if (Name(a, "--fresh"))
                {
                    o.Fresh = true;
                }
                else if (Name(a, "--no-compile"))
                {
                    o.NoCompile = true;
                }
                else if (Name(a, "--no-source"))
                {
                    o.SaveSource = false;
                }
                else if (Name(a, "--keep-source"))
                {
                    o.SaveSource = true;
                }
                else if (Name(a, "-i", "--input"))
                {
                    if (i + 1 < args.Length) o.InputPath = args[++i];
                }
                else if (Name(a, "-o", "--output"))
                {
                    if (i + 1 < args.Length) o.OutputPath = args[++i];
                }
                else if (Name(a, "--checkpoint"))
                {
                    if (i + 1 < args.Length)
                    {
                        int m;
                        if (int.TryParse(args[++i], out m)) o.CheckpointMinutes = m;
                    }
                }
                else if (StartsWith(a, "--input="))
                {
                    o.InputPath = a.Substring("--input=".Length);
                }
                else if (StartsWith(a, "--output="))
                {
                    o.OutputPath = a.Substring("--output=".Length);
                }
                else if (StartsWith(a, "--checkpoint="))
                {
                    int m;
                    if (int.TryParse(a.Substring("--checkpoint=".Length), out m)) o.CheckpointMinutes = m;
                }
                else if (!a.StartsWith("-") && string.IsNullOrEmpty(o.InputPath))
                {
                    o.InputPath = a;
                }
                else
                {
                    o.UnknownArgs.Add(a);
                }
            }

            if (string.IsNullOrEmpty(o.OutputPath))
                o.OutputPath = "nes_lifted_output";

            try { o.OutputPath = Path.GetFullPath(o.OutputPath); }
            catch { o.OutputPath = Path.Combine(Environment.CurrentDirectory, "nes_lifted_output"); }

            try
            {
                if (!string.IsNullOrEmpty(o.InputPath))
                    o.InputPath = Path.GetFullPath(o.InputPath);
            }
            catch { }

            return o;
        }

        static bool Name(string a, params string[] names)
        {
            foreach (string n in names)
            {
                if (string.Equals(a, n, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        static bool StartsWith(string a, string prefix)
        {
            return a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static class HelpPrinter
    {
        public static void Print()
        {
            Console.WriteLine();
            Console.WriteLine("NES Static Recompiler / Lifter to .NET Assembly");
            Console.WriteLine("================================================");
            Console.WriteLine();
            Console.WriteLine("Использование:");
            Console.WriteLine("  NesLifter.exe --input <rom.nes|папка> --output <папка> [опции]");
            Console.WriteLine();
            Console.WriteLine("Опции:");
            Console.WriteLine("  -i, --input <path>      Входной .nes файл или папка с ROM'ами.");
            Console.WriteLine("  -o, --output <dir>      Выходной каталог. По умолчанию: nes_lifted_output");
            Console.WriteLine("  -r, --recursive         Рекурсивная обработка папок.");
            Console.WriteLine("      --no-tray           Не создавать иконку в трее.");
            Console.WriteLine("      --wait              После завершения остаться в трее/ожидании.");
            Console.WriteLine("      --fresh             Игнорировать сохраненное состояние.");
            Console.WriteLine("      --no-compile        Только сгенерировать C#, не компилировать EXE.");
            Console.WriteLine("      --no-source         Не сохранять промежуточный C# (не рекомендуется).");
            Console.WriteLine("      --keep-source       Сохранять промежуточный C# (по умолчанию).");
            Console.WriteLine("      --checkpoint <min>  Интервал автосохранения состояния, мин. По умолчанию 10.");
            Console.WriteLine("  -h, --help              Эта справка.");
            Console.WriteLine();
            Console.WriteLine("Примеры:");
            Console.WriteLine("  NesLifter.exe game.nes");
            Console.WriteLine("  NesLifter.exe --input C:\\roms --output C:\\lifted -r --wait");
            Console.WriteLine("  NesLifter.exe --input C:\\roms -r --no-compile --checkpoint 5");
            Console.WriteLine();
            Console.WriteLine("Пайплайн:");
            Console.WriteLine("  1. Парсинг iNES: PRG ROM, CHR ROM, mapper, mirroring.");
            Console.WriteLine("  2. Дизассемблирование 6502, построение графа переходов.");
            Console.WriteLine("  3. Лифтинг инструкций в C#-код.");
            Console.WriteLine("  4. Dispatch-таблица для JMP indirect / RTS / RTI / BRK.");
            Console.WriteLine("  5. Генерация Memory Bus, PPU/APU stub, WinForms-окна.");
            Console.WriteLine("  6. Компиляция сгенерированного C# в Game.exe через CSharpCodeProvider.");
            Console.WriteLine();
        }
    }

    public static class ConsoleUI
    {
        public static bool CanUse()
        {
            return Environment.UserInteractive && !Console.IsOutputRedirected && !Console.IsInputRedirected;
        }

        public static int Select(string title, string[] options)
        {
            if (!CanUse() || options == null || options.Length == 0)
                return -1;

            int index = 0;
            while (true)
            {
                try
                {
                    Console.Clear();
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine(title);
                    Console.WriteLine("Используйте стрелки и Enter. Esc - выход.");
                    Console.ResetColor();
                    Console.WriteLine();

                    for (int i = 0; i < options.Length; i++)
                    {
                        if (i == index)
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine("> " + options[i]);
                            Console.ResetColor();
                        }
                        else
                        {
                            Console.WriteLine("  " + options[i]);
                        }
                    }

                    ConsoleKeyInfo key = Console.ReadKey(true);
                    if (key.Key == ConsoleKey.UpArrow)
                    {
                        index--;
                        if (index < 0) index = options.Length - 1;
                    }
                    else if (key.Key == ConsoleKey.DownArrow)
                    {
                        index++;
                        if (index >= options.Length) index = 0;
                    }
                    else if (key.Key == ConsoleKey.Enter)
                    {
                        return index;
                    }
                    else if (key.Key == ConsoleKey.Escape)
                    {
                        return -1;
                    }
                }
                catch
                {
                    return -1;
                }
            }
        }

        public static string AskPath()
        {
            Console.Write("Введите путь: ");
            string s = Console.ReadLine();
            return s == null ? string.Empty : s.Trim();
        }

        public static void Progress(string label, int current, int total)
        {
            if (!CanUse() || total <= 0) return;

            lock (typeof(ConsoleUI))
            {
                try
                {
                    if (current > total) current = total;
                    int width = 30;
                    int filled = (int)((long)current * width / total);
                    if (filled < 0) filled = 0;
                    if (filled > width) filled = width;

                    string bar = new string('#', filled) + new string('-', width - filled);
                    int percent = (int)((long)current * 100 / total);
                    string text = string.Format(
                        "\r[{0}] {1,3}% {2}/{3} {4}   ",
                        bar,
                        percent,
                        current,
                        total,
                        Truncate(label, 24));

                    Console.ForegroundColor = ConsoleColor.DarkCyan;
                    Console.Write(text);
                    Console.ResetColor();
                }
                catch { }
            }
        }

        public static void ProgressDone()
        {
            if (!CanUse()) return;
            try { Console.WriteLine(); } catch { }
        }

        static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            if (max <= 0) return string.Empty;
            if (s.Length <= max) return s;
            return s.Substring(0, max);
        }
    }

    public class TrayManager
    {
        TrayAppContext _context;
        Thread _thread;
        ManualResetEvent _exited = new ManualResetEvent(false);
        ManualResetEvent _ready = new ManualResetEvent(false);

        public void Start()
        {
            try
            {
                _thread = new Thread(new ThreadStart(Run));
                _thread.IsBackground = true;
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
                _ready.WaitOne(2000, false);
            }
            catch (Exception ex)
            {
                Log.Warn("Не удалось запустить трей: " + ex.Message);
            }
        }

        void Run()
        {
            try
            {
                _context = new TrayAppContext(this);
                _ready.Set();
                Application.Run(_context);
            }
            catch (Exception ex)
            {
                Log.Warn("Ошибка трея: " + ex.Message);
            }
            finally
            {
                _exited.Set();
                _ready.Set();
            }
        }

        public void SetStatus(string text)
        {
            TrayAppContext ctx = _context;
            if (ctx != null) ctx.SetStatus(text);
        }

        public void Shutdown()
        {
            TrayAppContext ctx = _context;
            if (ctx != null) ctx.RequestExit();
            _exited.WaitOne(2000, false);
        }

        public void WaitForExit()
        {
            _exited.WaitOne();
        }
    }

    public class TrayAppContext : ApplicationContext
    {
        NotifyIcon _notify;
        Form _invoker;
        TrayManager _manager;

        public TrayAppContext(TrayManager manager)
        {
            _manager = manager;

            _invoker = new Form();
            _invoker.ShowInTaskbar = false;
            _invoker.FormBorderStyle = FormBorderStyle.FixedToolWindow;
            _invoker.StartPosition = FormStartPosition.Manual;
            _invoker.Size = new Size(1, 1);
            _invoker.Opacity = 0;
            _invoker.Text = "NES Lifter Invoker";
            _invoker.Show();
            _invoker.Hide();
            MainForm = _invoker;

            _notify = new NotifyIcon();
            _notify.Icon = SystemIcons.Application;
            _notify.Text = "NES Lifter";

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Показать статус", null, delegate (object sender, EventArgs e)
            {
                Log.Info("Tray status: " + _notify.Text);
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Выход", null, delegate (object sender, EventArgs e)
            {
                RequestExit();
            });

            _notify.ContextMenuStrip = menu;
            _notify.DoubleClick += delegate (object sender, EventArgs e)
            {
                Log.Info("NES Lifter в трее. Используйте контекстное меню.");
            };
            _notify.Visible = true;
        }

        public void SetStatus(string text)
        {
            if (_notify == null) return;
            try { _notify.Text = Truncate(text, 63); } catch { }
        }

        public void RequestExit()
        {
            if (_invoker != null && _invoker.IsHandleCreated)
                _invoker.BeginInvoke(new MethodInvoker(ExitThread));
            else
                ExitThread();
        }

        string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "NES Lifter";
            if (s.Length <= max) return s;
            return s.Substring(0, max);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_notify != null)
                {
                    try
                    {
                        _notify.Visible = false;
                        _notify.Dispose();
                    }
                    catch { }
                    _notify = null;
                }
            }
            base.Dispose(disposing);
        }
    }

    [XmlRoot("NesLifterState")]
    public class AppState
    {
        public int Version = 1;
        public DateTime Updated = DateTime.Now;
        public string LastFile = string.Empty;
        public List<string> ProcessedFiles = new List<string>();
    }

    public class StateManager
    {
        Options _opts;
        AppState _state = new AppState();
        string _path;
        object _lock = new object();
        System.Threading.Timer _timer;

        public StateManager(Options opts)
        {
            _opts = opts;
            _path = Path.Combine(opts.OutputPath, ".neslifter.state.xml");
        }

        public void Load()
        {
            try
            {
                if (_opts.Fresh)
                {
                    if (File.Exists(_path))
                    {
                        File.Delete(_path);
                        Log.Info("Состояние сброшено (--fresh).");
                    }
                    _state = new AppState();
                    return;
                }

                if (!File.Exists(_path))
                {
                    _state = new AppState();
                    return;
                }

                XmlSerializer ser = new XmlSerializer(typeof(AppState));
                FileStream fs = new FileStream(_path, FileMode.Open, FileAccess.Read);
                try
                {
                    _state = (AppState)ser.Deserialize(fs);
                }
                finally
                {
                    fs.Close();
                }

                if (_state == null) _state = new AppState();
                if (_state.ProcessedFiles == null) _state.ProcessedFiles = new List<string>();

                Log.Info("Загружено состояние. Уже обработано файлов: " + _state.ProcessedFiles.Count);
            }
            catch (Exception ex)
            {
                Log.Warn("Не удалось загрузить состояние: " + ex.Message);
                try
                {
                    if (File.Exists(_path))
                        File.Move(_path, _path + ".corrupt");
                }
                catch { }
                _state = new AppState();
            }
        }

        public void Save()
        {
            lock (_lock)
            {
                try
                {
                    string dir = Path.GetDirectoryName(_path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    _state.Updated = DateTime.Now;
                    XmlSerializer ser = new XmlSerializer(typeof(AppState));
                    string tmp = _path + ".tmp";

                    FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write);
                    try
                    {
                        ser.Serialize(fs, _state);
                    }
                    finally
                    {
                        fs.Close();
                    }

                    if (File.Exists(_path)) File.Delete(_path);
                    File.Move(tmp, _path);
                }
                catch (Exception ex)
                {
                    Log.Warn("Не удалось сохранить состояние: " + ex.Message);
                }
            }
        }

        public bool IsProcessed(string file)
        {
            lock (_lock)
            {
                return _state.ProcessedFiles.Contains(Normalize(file));
            }
        }

        public void MarkProcessed(string file)
        {
            lock (_lock)
            {
                string n = Normalize(file);
                if (!_state.ProcessedFiles.Contains(n))
                    _state.ProcessedFiles.Add(n);
                _state.LastFile = file;
                Save();
            }
        }

        public void SetLastFile(string file)
        {
            lock (_lock)
            {
                _state.LastFile = file;
            }
        }

        public void StartTimer()
        {
            if (_opts.CheckpointMinutes <= 0) return;

            long msLong = (long)_opts.CheckpointMinutes * 60000L;
            int ms = msLong > int.MaxValue ? int.MaxValue : (int)msLong;
            if (ms < 1000) ms = 1000;

            _timer = new System.Threading.Timer(new TimerCallback(OnTimer), null, ms, ms);
            Log.Info("Чекпоинт состояния каждые " + _opts.CheckpointMinutes + " мин.");
        }

        public void StopTimer()
        {
            if (_timer != null)
            {
                try { _timer.Dispose(); } catch { }
                _timer = null;
            }
        }

        void OnTimer(object state)
        {
            Save();
            Log.Debug("Checkpoint state saved.");
        }

        string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            return path.ToUpperInvariant();
        }
    }

    public class Pipeline
    {
        Options _opts;
        StateManager _state;

        public Pipeline(Options opts, StateManager state)
        {
            _opts = opts;
            _state = state;
        }

        public int Run()
        {
            Status.Set("Инициализация");
            EnsureOutput();
            EnsureConfig();

            List<string> files = CollectFiles();
            if (files.Count == 0)
            {
                Log.Warn("Не найдено входных .nes файлов.");
                return 1;
            }

            Log.Info("Найдено файлов: " + files.Count);

            int ok = 0;
            int failed = 0;
            int skipped = 0;

            for (int i = 0; i < files.Count; i++)
            {
                string file = files[i];
                string full = Path.GetFullPath(file);

                if (_state.IsProcessed(full))
                {
                    skipped++;
                    Log.Info("Пропуск уже обработанного: " + file);
                    ConsoleUI.Progress(Path.GetFileName(file), i + 1, files.Count);
                    continue;
                }

                Status.Set("Обработка " + Path.GetFileName(file));
                _state.SetLastFile(full);
                ConsoleUI.Progress(Path.GetFileName(file), i, files.Count);

                bool success = SafeProcess(file);
                if (success)
                {
                    _state.MarkProcessed(full);
                    ok++;
                }
                else
                {
                    failed++;
                }

                ConsoleUI.Progress(Path.GetFileName(file), i + 1, files.Count);
            }

            ConsoleUI.ProgressDone();
            Status.Set("Готово");
            Log.Info(string.Format("Итог: успешно={0}, ошибок={1}, пропущено={2}", ok, failed, skipped));

            return failed == 0 ? 0 : 2;
        }

        void EnsureOutput()
        {
            Directory.CreateDirectory(_opts.OutputPath);
        }

        void EnsureConfig()
        {
            try
            {
                string cfg = Path.Combine(_opts.OutputPath, "neslifter.config.ini");
                if (!File.Exists(cfg))
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("; NesLifter auto-created config");
                    sb.AppendLine("; Эти значения используются как справочные/дефолтные.");
                    sb.AppendLine("Recursive=" + (_opts.Recursive ? "true" : "false"));
                    sb.AppendLine("CheckpointMinutes=" + _opts.CheckpointMinutes);
                    sb.AppendLine("SaveSource=" + (_opts.SaveSource ? "true" : "false"));
                    sb.AppendLine("NoCompile=" + (_opts.NoCompile ? "true" : "false"));
                    File.WriteAllText(cfg, sb.ToString(), Encoding.UTF8);
                    Log.Info("Создан конфиг: " + cfg);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Не удалось создать конфиг: " + ex.Message);
            }
        }

        List<string> CollectFiles()
        {
            List<string> files = new List<string>();

            try
            {
                if (File.Exists(_opts.InputPath))
                {
                    files.Add(Path.GetFullPath(_opts.InputPath));
                }
                else if (Directory.Exists(_opts.InputPath))
                {
                    SearchOption so = _opts.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                    files.AddRange(Directory.GetFiles(_opts.InputPath, "*.nes", so));
                }
                else
                {
                    Log.Error("Входной путь не найден: " + _opts.InputPath);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Ошибка сбора файлов: " + ex.Message);
            }

            files.Sort(StringComparer.OrdinalIgnoreCase);
            return files;
        }

        bool SafeProcess(string file)
        {
            try
            {
                return ProcessFile(file);
            }
            catch (Exception ex)
            {
                Log.Error("Ошибка обработки файла: " + file);
                Log.Error(ex.Message);
                Log.Debug(ex.ToString());
                return false;
            }
        }

        bool ProcessFile(string file)
        {
            string romName = Path.GetFileNameWithoutExtension(file);
            string safeName = FileSystemUtil.SanitizeFileName(romName);
            string workDir = Path.Combine(_opts.OutputPath, safeName);
            string srcDir = Path.Combine(workDir, "src");

            Directory.CreateDirectory(workDir);
            if (_opts.SaveSource) Directory.CreateDirectory(srcDir);

            Log.Step("=== Обработка ROM: " + file + " ===");

            NesRom rom = NesRom.Load(file);
            Log.Info(string.Format(
                "iNES: PRG={0} bytes, CHR={1} bytes, Mapper={2}, Mirroring={3}, Battery={4}, Trainer={5}",
                rom.PrgRom.Length,
                rom.ChrRom.Length,
                rom.Mapper,
                rom.Mirroring,
                rom.HasBattery,
                rom.HasTrainer));

            Log.Info(string.Format(
                "Vectors: NMI=0x{0:X4}, RESET=0x{1:X4}, IRQ=0x{2:X4}",
                rom.ReadVector(0xFFFA),
                rom.ReadVector(0xFFFC),
                rom.ReadVector(0xFFFE)));

            Disassembler dis = new Disassembler(rom);

            // ==========================================================
            // Forced dynamic targets
            // ==========================================================
            // Сюда можно вручную добавлять адреса, которые обнаружились
            // только во время выполнения, например:
            // Dynamic JMP at $0006 -> $8231
            dis.ForcedAddresses.Add(0x8231);

            string dynamicTargetsPath = Path.Combine(workDir, "dynamic_targets.txt");

            if (!File.Exists(dynamicTargetsPath))
            {
                try
                {
                    File.WriteAllText(
                        dynamicTargetsPath,
                        "; Hex addresses for forced disassembly.\r\n" +
                        "; Format examples:\r\n" +
                        "; 8231\r\n" +
                        "; 0x8231\r\n" +
                        "; $8231\r\n" +
                        "\r\n" +
                        "8231\r\n",
                        Encoding.UTF8);

                    Log.Info("Создан файл dynamic targets: " + dynamicTargetsPath);
                }
                catch (Exception ex)
                {
                    Log.Warn("Не удалось создать dynamic_targets.txt: " + ex.Message);
                }
            }

            string[] dynamicTargetFiles = new string[]
            {
                Path.Combine(workDir, "dynamic_targets.txt"),
                Path.Combine(workDir, "dynamic_targets.log")
            };

            foreach (string dynamicTargetsFile in dynamicTargetFiles)
            {
                if (!File.Exists(dynamicTargetsFile))
                    continue;

                try
                {
                    foreach (string rawLine in File.ReadAllLines(dynamicTargetsFile))
                    {
                        string line = rawLine.Trim();

                        if (line.Length == 0)
                            continue;

                        if (line.StartsWith(";") || line.StartsWith("#"))
                            continue;

                        line = line.Replace("0x", "").Replace("$", "").Trim();

                        ushort addr;
                        if (ushort.TryParse(line, NumberStyles.HexNumber, null, out addr))
                        {
                            if (!dis.ForcedAddresses.Contains(addr))
                                dis.ForcedAddresses.Add(addr);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("Не удалось прочитать dynamic targets file: " + ex.Message);
                }
            }

            if (dis.ForcedAddresses.Count > 0)
            {
                Log.Info("Forced dynamic targets count: " + dis.ForcedAddresses.Count);
            }

            // Временно отключаем агрессивный pointer scan.
            // Он дал слишком много ложных целей и раздул исходник до 38 MB.
            AnalysisResult model = dis.Analyze();

            Log.Info(string.Format(
                "Анализ: instructions={0}, labels={1}, functions~={2}, unknownOps={3}, indirectJumps={4}",
                model.Instructions.Count,
                model.Labels.Count,
                model.Functions.Count,
                model.UnknownOpcodes.Count,
                model.IndirectJumps.Count));

            foreach (ushort addr in model.IndirectJumps)
            {
                Log.Warn("Indirect JMP at: 0x" + addr.ToString("X4"));
            }

            foreach (byte op in model.UnknownOpcodes)
            {
                Log.Warn("Unknown opcode: 0x" + op.ToString("X2"));
            }

            if (model.UnknownOpcodes.Count > 0)
                Log.Warn("Найдены неизвестные/неподдерживаемые опкоды. Они будут заменены trap/NOP-заглушками.");

            if (model.IndirectJumps.Count > 0)
                Log.Warn("Найдены JMP ($addr). Используется dispatch-таблица динамических переходов.");

            Lifter lifter = new Lifter(rom, model, safeName);
            string code = lifter.Generate();

            if (_opts.SaveSource)
            {
                string srcPath = Path.Combine(srcDir, "Game.generated.cs");
                File.WriteAllText(srcPath, code, Encoding.UTF8);
                Log.Ok("Промежуточный C# сохранен: " + srcPath);
            }

            if (_opts.NoCompile)
            {
                Log.Warn("Компиляция отключена (--no-compile).");
                return true;
            }

            string exePath = Path.Combine(workDir, safeName + ".exe");
            bool compiled = Compile(code, exePath);

            if (compiled)
                Log.Ok("Скомпилировано: " + exePath);
            else
                Log.Error("Компиляция сгенерированного кода не удалась. Смотрите лог и src/Game.generated.cs.");

            return compiled;
        }

        bool Compile(string code, string exePath)
        {
            try
            {
                if (File.Exists(exePath))
                {
                    try { File.Delete(exePath); } catch { }
                }

                CSharpCodeProvider provider = new CSharpCodeProvider();
                try
                {
                    CompilerParameters cp = new CompilerParameters();
                    cp.GenerateExecutable = true;
                    cp.OutputAssembly = exePath;
                    cp.IncludeDebugInformation = false;
                    cp.CompilerOptions = "/optimize- /nowarn:0162,0164,0219";
                    cp.ReferencedAssemblies.Add("System.dll");
                    cp.ReferencedAssemblies.Add("System.Drawing.dll");
                    cp.ReferencedAssemblies.Add("System.Windows.Forms.dll");

                    CompilerResults res = provider.CompileAssemblyFromSource(cp, code);

                    if (res.Errors.HasErrors)
                    {
                        foreach (CompilerError err in res.Errors)
                        {
                            if (err.IsWarning)
                            {
                                Log.Warn(string.Format("Compile warning {0}: {1}", err.ErrorNumber, err.ErrorText));
                            }
                            else
                            {
                                Log.Error(string.Format("Compile error {0} at ({1},{2}): {3}",
                                    err.ErrorNumber, err.Line, err.Column, err.ErrorText));
                            }
                        }
                        return false;
                    }

                    return true;
                }
                finally
                {
                    provider.Dispose();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Исключение при компиляции: " + ex.Message);
                Log.Debug(ex.ToString());
                return false;
            }
        }
    }

    public static class FileSystemUtil
    {
        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "rom";

            StringBuilder sb = new StringBuilder();
            char[] invalid = Path.GetInvalidFileNameChars();

            foreach (char c in name)
            {
                if (Array.IndexOf(invalid, c) >= 0) sb.Append('_');
                else sb.Append(c);
            }

            string s = sb.ToString().Trim();
            if (s.Length == 0) s = "rom";
            return s;
        }

        public static string SanitizeIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Game";

            StringBuilder sb = new StringBuilder();
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
                else sb.Append('_');
            }

            if (sb.Length == 0) sb.Append("Game");
            if (char.IsDigit(sb[0])) sb.Insert(0, '_');

            return sb.ToString();
        }
    }

    public class NesRom
    {
        public byte[] PrgRom = new byte[0];
        public byte[] ChrRom = new byte[0];
        public int Mapper;
        public string Mirroring = "Unknown";
        public bool HasBattery;
        public bool HasTrainer;
        public byte Flags6;
        public byte Flags7;

        public static NesRom Load(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (fs.Length < 16)
                    throw new InvalidDataException("Файл меньше iNES заголовка.");

                byte[] header = new byte[16];
                ReadExact(fs, header, 16);

                if (header[0] != 0x4E || header[1] != 0x45 || header[2] != 0x53 || header[3] != 0x1A)
                    throw new InvalidDataException("Нет сигнатуры iNES (NES 0x1A).");

                int prgBanks = header[4];
                int chrBanks = header[5];
                byte flags6 = header[6];
                byte flags7 = header[7];

                NesRom rom = new NesRom();
                rom.Flags6 = flags6;
                rom.Flags7 = flags7;
                rom.Mapper = ((flags7 & 0xF0) | (flags6 >> 4));
                rom.HasTrainer = (flags6 & 0x04) != 0;
                rom.HasBattery = (flags6 & 0x02) != 0;

                if ((flags6 & 0x08) != 0) rom.Mirroring = "Four-screen";
                else if ((flags6 & 0x01) != 0) rom.Mirroring = "Vertical";
                else rom.Mirroring = "Horizontal";

                if (rom.HasTrainer)
                {
                    byte[] trainer = new byte[512];
                    ReadExact(fs, trainer, 512);
                }

                long prgSize = (long)prgBanks * 16384L;
                long chrSize = (long)chrBanks * 8192L;

                rom.PrgRom = ReadBlock(fs, prgSize);
                rom.ChrRom = ReadBlock(fs, chrSize);

                return rom;
            }
        }

        static void ReadExact(FileStream fs, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = fs.Read(buffer, read, count - read);
                if (n <= 0) throw new EndOfStreamException("Неожиданный конец ROM-файла.");
                read += n;
            }
        }

        static byte[] ReadBlock(FileStream fs, long size)
        {
            if (size == 0) return new byte[0];
            if (size > int.MaxValue) throw new InvalidDataException("Слишком большой блок ROM для данной реализации.");

            byte[] data = new byte[(int)size];
            int read = 0;
            while (read < data.Length)
            {
                int n = fs.Read(data, read, data.Length - read);
                if (n <= 0) throw new EndOfStreamException("Неожиданный конец ROM-файла.");
                read += n;
            }
            return data;
        }

        public int AddrToOffset(ushort addr)
        {
            if (addr < 0x8000) return -1;
            int len = PrgRom.Length;
            if (len == 0) return -1;

            // Mapper 0, 16KB: зеркалим $8000-$BFFF и $C000-$FFFF
            if (len == 0x4000)
            {
                if (addr >= 0xC000)
                    return addr - 0xC000;
                else
                    return addr - 0x8000;
            }

            // Mapper 0, 32KB: линейно $8000-$FFFF
            if (len == 0x8000)
            {
                return addr - 0x8000;
            }

            // Для больших PRG (банковые мапперы): последняя 32KB фиксирована
            if (len > 0x8000)
            {
                int baseIndex = len - 0x8000;
                int off = baseIndex + (addr - 0x8000);
                if (off >= 0 && off < len) return off;
                return -1;
            }

            // Нестандартный размер: модульное зеркалирование
            int a = addr >= 0xC000 ? addr - 0xC000 : addr - 0x8000;
            if (a < 0) a = addr - 0x8000;
            return a % len;
        }

        public ushort ReadVector(ushort vectorAddr)
        {
            // Вектора всегда в последних 6 байтах PRG ROM
            // $FFFA/$FFFB = NMI, $FFFC/$FFFD = RESET, $FFFE/$FFFF = IRQ
            int len = PrgRom.Length;
            if (len < 6) return 0;

            int off;
            if (vectorAddr == 0xFFFA) off = len - 6;
            else if (vectorAddr == 0xFFFC) off = len - 4;
            else if (vectorAddr == 0xFFFE) off = len - 2;
            else
            {
                // Fallback через AddrToOffset
                off = AddrToOffset(vectorAddr);
                if (off < 0 || off + 1 >= len) return 0;
            }

            return (ushort)(PrgRom[off] | (PrgRom[off + 1] << 8));
        }
    }

    public enum AddrMode
    {
        Imp,
        Acc,
        Imm,
        Zp,
        ZpX,
        ZpY,
        Abs,
        AbsX,
        AbsY,
        Ind,
        XInd,
        IndY,
        Rel
    }

    public enum OpControl
    {
        Normal,
        Branch,
        Jmp,
        JmpInd,
        Jsr,
        Rts,
        Rti,
        Brk,
        Invalid
    }

    public class OpInfo
    {
        public byte Opcode;
        public string Mn;
        public byte Len;
        public AddrMode Mode;
        public OpControl Ctrl;

        public OpInfo(byte opcode, string mn, byte len, AddrMode mode, OpControl ctrl)
        {
            Opcode = opcode;
            Mn = mn;
            Len = len;
            Mode = mode;
            Ctrl = ctrl;
        }
    }

    public static class Cpu6502
    {
        public static OpInfo[] Table = new OpInfo[256];

        static Cpu6502()
        {
            DefineTable();
        }

        static void Def(int op, string mn, byte len, AddrMode mode, OpControl ctrl)
        {
            Table[op] = new OpInfo((byte)op, mn, len, mode, ctrl);
        }

        static void DefineTable()
        {
            Def(0x00, "BRK", 1, AddrMode.Imp, OpControl.Brk);
            Def(0x01, "ORA", 2, AddrMode.XInd, OpControl.Normal);
            Def(0x05, "ORA", 2, AddrMode.Zp, OpControl.Normal);
            Def(0x06, "ASL", 2, AddrMode.Zp, OpControl.Normal);
            Def(0x08, "PHP", 1, AddrMode.Imp, OpControl.Normal);
            Def(0x0A, "ASL", 1, AddrMode.Acc, OpControl.Normal);
            Def(0x0D, "ORA", 3, AddrMode.Abs, OpControl.Normal);
            Def(0x0E, "ASL", 3, AddrMode.Abs, OpControl.Normal);

            Def(0x10, "BPL", 2, AddrMode.Rel, OpControl.Branch);
            Def(0x11, "ORA", 2, AddrMode.IndY, OpControl.Normal);
            Def(0x15, "ORA", 2, AddrMode.ZpX, OpControl.Normal);
            Def(0x16, "ASL", 2, AddrMode.ZpX, OpControl.Normal);
            Def(0x18, "CLC", 1, AddrMode.Imp, OpControl.Normal);
            Def(0x19, "ORA", 3, AddrMode.AbsY, OpControl.Normal);
            Def(0x1D, "ORA", 3, AddrMode.AbsX, OpControl.Normal);
            Def(0x1E, "ASL", 3, AddrMode.AbsX, OpControl.Normal);

            Def(0x20, "JSR", 3, AddrMode.Abs, OpControl.Jsr);
            Def(0x21, "AND", 2, AddrMode.XInd, OpControl.Normal);
            Def(0x24, "BIT", 2, AddrMode.Zp, OpControl.Normal);
            Def(0x25, "AND", 2, AddrMode.Zp, OpControl.Normal);
            Def(0x26, "ROL", 2, AddrMode.Zp, OpControl.Normal);
            Def(0x28, "PLP", 1, AddrMode.Imp, OpControl.Normal);
            Def(0x2A, "ROL", 1, AddrMode.Acc, OpControl.Normal);
            Def(0x2C, "BIT", 3, AddrMode.Abs, OpControl.Normal);
            Def(0x2D, "AND", 3, AddrMode.Abs, OpControl.Normal);
            Def(0x2E, "ROL", 3, AddrMode.Abs, OpControl.Normal);

            Def(0x30, "BMI", 2, AddrMode.Rel, OpControl.Branch);
            Def(0x31, "AND", 2, AddrMode.IndY, OpControl.Normal);
            Def(0x35, "AND", 2, AddrMode.ZpX, OpControl.Normal);
            Def(0x36, "ROL", 2, AddrMode.ZpX, OpControl.Normal);
            Def(0x38, "SEC", 1, AddrMode.Imp, OpControl.Normal);
            Def(0x39, "AND", 3, AddrMode.AbsY, OpControl.Normal);
            Def(0x3D, "AND", 3, AddrMode.AbsX, OpControl.Normal);
            Def(0x3E, "ROL", 3, AddrMode.AbsX, OpControl.Normal);

            Def(0x40, "RTI", 1, AddrMode.Imp, OpControl.Rti);
            Def(0x41, "EOR", 2, AddrMode.XInd, OpControl.Normal);
            Def(0x45, "EOR", 2, AddrMode.Zp, OpControl.Normal);
            Def(0x46, "LSR", 2, AddrMode.Zp, OpControl.Normal);
            Def(0x48, "PHA", 1, AddrMode.Imp, OpControl.Normal);
            Def(0x4A, "LSR", 1, AddrMode.Acc, OpControl.Normal);
            Def(0x4C, "JMP", 3, AddrMode.Abs, OpControl.Jmp);
            Def(0x4D, "EOR", 3, AddrMode.Abs, OpControl.Normal);
            Def(0x4E, "LSR", 3, AddrMode.Abs, OpControl.Normal);

            Def(0x50, "BVC", 2, AddrMode.Rel, OpControl.Branch);
            Def(0x51, "EOR", 2, AddrMode.IndY, OpControl.Normal);
            Def(0x55, "EOR", 2, AddrMode.ZpX, OpControl.Normal);
            Def(0x56, "LSR", 2, AddrMode.ZpX, OpControl.Normal);
            Def(0x58, "CLI", 1, AddrMode.Imp, OpControl.Normal);
            Def(0x59, "EOR", 3, AddrMode.AbsY, OpControl.Normal);
            Def(0x5D, "EOR", 3, AddrMode.AbsX, OpControl.Normal);
            Def(0x5E, "LSR", 3, AddrMode.AbsX, OpControl.Normal);

            Def(0x60, "RTS", 1, AddrMode.Imp, OpControl.Rts);
            Def(0x61, "ADC", 2, AddrMode.XInd, OpControl.Normal);
            Def(0x65, "ADC", 2, AddrMode.Zp, OpControl.Normal);
            Def(0x66, "ROR", 2, AddrMode.Zp, OpControl.Normal);
            Def(0x68, "PLA", 1, AddrMode.Imp, OpControl.Normal);
            Def(0x6A, "ROR", 1, AddrMode.Acc, OpControl.Normal);
            Def(0x6C, "JMP", 3, AddrMode.Ind, OpControl.JmpInd);
            Def(0x6D, "ADC", 3, AddrMode.Abs, OpControl.Normal);
            Def(0x6E, "ROR", 3, AddrMode.Abs, OpControl.Normal);

            Def(0x70, "BVS", 2, AddrMode.Rel, OpControl.Branch);
            Def(0x71, "ADC", 2, AddrMode.IndY, OpControl.Normal);
            Def(0x75, "ADC", 2, AddrMode.ZpX, OpControl.Normal);
            Def(0x76, "ROR", 2, AddrMode.ZpX, OpControl.Normal);
            Def(0x78, "SEI", 1, AddrMode.Imp, OpControl.Normal);
            Def(0x79, "ADC", 3, AddrMode.AbsY, OpControl.Normal);
            Def(0x7D, "ADC", 3, AddrMode.AbsX, OpControl.Normal);
            Def(0x7E, "ROR", 3, AddrMode.AbsX, OpControl.Normal);

            Def(0x81, "STA", 2, AddrMode.XInd, OpControl.Normal);
            Def(0x84, "STY", 2, AddrMode.Zp, OpControl.Normal);
            Def(0x85, "STA", 2, AddrMode.Zp, OpControl.Normal);
            Def(0x86, "STX", 2, AddrMode.Zp, OpControl.Normal);
            Def(0x88, "DEY", 1, AddrMode.Imp, OpControl.Normal);
            Def(0x8A, "TXA", 1, AddrMode.Imp, OpControl.Normal);
            Def(0x8C, "STY", 3, AddrMode.Abs, OpControl.Normal);
            Def(0x8D, "STA", 3, AddrMode.Abs, OpControl.Normal);
            Def(0x8E, "STX", 3, AddrMode.Abs, OpControl.Normal);

            Def(0x90, "BCC", 2, AddrMode.Rel, OpControl.Branch);
            Def(0x91, "STA", 2, AddrMode.IndY, OpControl.Normal);
            Def(0x94, "STY", 2, AddrMode.ZpX, OpControl.Normal);
            Def(0x95, "STA", 2, AddrMode.ZpX, OpControl.Normal);
            Def(0x96, "STX", 2, AddrMode.ZpY, OpControl.Normal);
            Def(0x98, "TYA", 1, AddrMode.Imp, OpControl.Normal);
            Def(0x99, "STA", 3, AddrMode.AbsY, OpControl.Normal);
            Def(0x9A, "TXS", 1, AddrMode.Imp, OpControl.Normal);
            Def(0x9D, "STA", 3, AddrMode.AbsX, OpControl.Normal);

            Def(0xA0, "LDY", 2, AddrMode.Imm, OpControl.Normal);
            Def(0xA1, "LDA", 2, AddrMode.XInd, OpControl.Normal);
            Def(0xA2, "LDX", 2, AddrMode.Imm, OpControl.Normal);
            Def(0xA4, "LDY", 2, AddrMode.Zp, OpControl.Normal);
            Def(0xA5, "LDA", 2, AddrMode.Zp, OpControl.Normal);
            Def(0xA6, "LDX", 2, AddrMode.Zp, OpControl.Normal);
            Def(0xA8, "TAY", 1, AddrMode.Imp, OpControl.Normal);
            Def(0xAA, "TAX", 1, AddrMode.Imp, OpControl.Normal);
            Def(0xAC, "LDY", 3, AddrMode.Abs, OpControl.Normal);
            Def(0xAD, "LDA", 3, AddrMode.Abs, OpControl.Normal);
            Def(0xAE, "LDX", 3, AddrMode.Abs, OpControl.Normal);

            Def(0xB0, "BCS", 2, AddrMode.Rel, OpControl.Branch);
            Def(0xB1, "LDA", 2, AddrMode.IndY, OpControl.Normal);
            Def(0xB4, "LDY", 2, AddrMode.ZpX, OpControl.Normal);
            Def(0xB5, "LDA", 2, AddrMode.ZpX, OpControl.Normal);
            Def(0xB6, "LDX", 2, AddrMode.ZpY, OpControl.Normal);
            Def(0xB8, "CLV", 1, AddrMode.Imp, OpControl.Normal);
            Def(0xB9, "LDA", 3, AddrMode.AbsY, OpControl.Normal);
            Def(0xBA, "TSX", 1, AddrMode.Imp, OpControl.Normal);
            Def(0xBC, "LDY", 3, AddrMode.AbsX, OpControl.Normal);
            Def(0xBD, "LDA", 3, AddrMode.AbsX, OpControl.Normal);
            Def(0xBE, "LDX", 3, AddrMode.AbsY, OpControl.Normal);

            Def(0xC0, "CPY", 2, AddrMode.Imm, OpControl.Normal);
            Def(0xC1, "CMP", 2, AddrMode.XInd, OpControl.Normal);
            Def(0xC4, "CPY", 2, AddrMode.Zp, OpControl.Normal);
            Def(0xC5, "CMP", 2, AddrMode.Zp, OpControl.Normal);
            Def(0xC6, "DEC", 2, AddrMode.Zp, OpControl.Normal);
            Def(0xC8, "INY", 1, AddrMode.Imp, OpControl.Normal);
            Def(0xCA, "DEX", 1, AddrMode.Imp, OpControl.Normal);
            Def(0xCC, "CPY", 3, AddrMode.Abs, OpControl.Normal);
            Def(0xCD, "CMP", 3, AddrMode.Abs, OpControl.Normal);
            Def(0xCE, "DEC", 3, AddrMode.Abs, OpControl.Normal);

            Def(0xD0, "BNE", 2, AddrMode.Rel, OpControl.Branch);
            Def(0xD1, "CMP", 2, AddrMode.IndY, OpControl.Normal);
            Def(0xD5, "CMP", 2, AddrMode.ZpX, OpControl.Normal);
            Def(0xD6, "DEC", 2, AddrMode.ZpX, OpControl.Normal);
            Def(0xD8, "CLD", 1, AddrMode.Imp, OpControl.Normal);
            Def(0xD9, "CMP", 3, AddrMode.AbsY, OpControl.Normal);
            Def(0xDD, "CMP", 3, AddrMode.AbsX, OpControl.Normal);
            Def(0xDE, "DEC", 3, AddrMode.AbsX, OpControl.Normal);

            Def(0xE0, "CPX", 2, AddrMode.Imm, OpControl.Normal);
            Def(0xE1, "SBC", 2, AddrMode.XInd, OpControl.Normal);
            Def(0xE4, "CPX", 2, AddrMode.Zp, OpControl.Normal);
            Def(0xE5, "SBC", 2, AddrMode.Zp, OpControl.Normal);
            Def(0xE6, "INC", 2, AddrMode.Zp, OpControl.Normal);
            Def(0xE8, "INX", 1, AddrMode.Imp, OpControl.Normal);
            Def(0xEA, "NOP", 1, AddrMode.Imp, OpControl.Normal);
            Def(0xEC, "CPX", 3, AddrMode.Abs, OpControl.Normal);
            Def(0xED, "SBC", 3, AddrMode.Abs, OpControl.Normal);
            Def(0xEE, "INC", 3, AddrMode.Abs, OpControl.Normal);

            Def(0xF0, "BEQ", 2, AddrMode.Rel, OpControl.Branch);
            Def(0xF1, "SBC", 2, AddrMode.IndY, OpControl.Normal);
            Def(0xF5, "SBC", 2, AddrMode.ZpX, OpControl.Normal);
            Def(0xF6, "INC", 2, AddrMode.ZpX, OpControl.Normal);
            Def(0xF8, "SED", 1, AddrMode.Imp, OpControl.Normal);
            Def(0xF9, "SBC", 3, AddrMode.AbsY, OpControl.Normal);
            Def(0xFD, "SBC", 3, AddrMode.AbsX, OpControl.Normal);
            Def(0xFE, "INC", 3, AddrMode.AbsX, OpControl.Normal);

            // === IMMEDIATE MODE (пропущенные) ===
            Def(0x09, "ORA", 2, AddrMode.Imm, OpControl.Normal);
            Def(0x29, "AND", 2, AddrMode.Imm, OpControl.Normal);
            Def(0x49, "EOR", 2, AddrMode.Imm, OpControl.Normal);
            Def(0x69, "ADC", 2, AddrMode.Imm, OpControl.Normal);
            Def(0xA9, "LDA", 2, AddrMode.Imm, OpControl.Normal);
            Def(0xC9, "CMP", 2, AddrMode.Imm, OpControl.Normal);
            Def(0xE0, "CPX", 2, AddrMode.Imm, OpControl.Normal);
            Def(0xC0, "CPY", 2, AddrMode.Imm, OpControl.Normal);
            Def(0xE9, "SBC", 2, AddrMode.Imm, OpControl.Normal);

            // === ДОПОЛНИТЕЛЬНЫЕ ABS,X / ABS,Y (пропущенные) ===
            Def(0x1C, "NOP", 3, AddrMode.AbsX, OpControl.Normal); // unofficial NOP, но встречается
            Def(0x3C, "NOP", 3, AddrMode.AbsX, OpControl.Normal);
            Def(0x5C, "NOP", 3, AddrMode.AbsX, OpControl.Normal);
            Def(0x7C, "NOP", 3, AddrMode.AbsX, OpControl.Normal);
            Def(0xDC, "NOP", 3, AddrMode.AbsX, OpControl.Normal);
            Def(0xFC, "NOP", 3, AddrMode.AbsX, OpControl.Normal);

            // === UNOFFICIAL NOP (1-byte и 2-byte) ===
            Def(0x1A, "NOP", 1, AddrMode.Imp, OpControl.Normal);
            Def(0x3A, "NOP", 1, AddrMode.Imp, OpControl.Normal);
            Def(0x5A, "NOP", 1, AddrMode.Imp, OpControl.Normal);
            Def(0x7A, "NOP", 1, AddrMode.Imp, OpControl.Normal);
            Def(0xDA, "NOP", 1, AddrMode.Imp, OpControl.Normal);
            Def(0xFA, "NOP", 1, AddrMode.Imp, OpControl.Normal);
            Def(0x80, "NOP", 2, AddrMode.Imm, OpControl.Normal);
            Def(0x82, "NOP", 2, AddrMode.Imm, OpControl.Normal);
            Def(0x89, "NOP", 2, AddrMode.Imm, OpControl.Normal);
            Def(0xC2, "NOP", 2, AddrMode.Imm, OpControl.Normal);
            Def(0xE2, "NOP", 2, AddrMode.Imm, OpControl.Normal);
            Def(0x04, "NOP", 2, AddrMode.Zp, OpControl.Normal);
            Def(0x44, "NOP", 2, AddrMode.Zp, OpControl.Normal);
            Def(0x64, "NOP", 2, AddrMode.Zp, OpControl.Normal);
            Def(0x14, "NOP", 2, AddrMode.ZpX, OpControl.Normal);
            Def(0x34, "NOP", 2, AddrMode.ZpX, OpControl.Normal);
            Def(0x54, "NOP", 2, AddrMode.ZpX, OpControl.Normal);
            Def(0x74, "NOP", 2, AddrMode.ZpX, OpControl.Normal);
            Def(0xD4, "NOP", 2, AddrMode.ZpX, OpControl.Normal);
            Def(0xF4, "NOP", 2, AddrMode.ZpX, OpControl.Normal);
            Def(0x0C, "NOP", 3, AddrMode.Abs, OpControl.Normal);

            // === UNOFFICIAL LAX/SAX/DCP/ISB/SLO/RLA/SRE/RRA (частые в NES) ===
            Def(0xA7, "LAX", 2, AddrMode.Zp, OpControl.Normal);
            Def(0xB7, "LAX", 2, AddrMode.ZpY, OpControl.Normal);
            Def(0xAF, "LAX", 3, AddrMode.Abs, OpControl.Normal);
            Def(0xBF, "LAX", 3, AddrMode.AbsY, OpControl.Normal);
            Def(0xA3, "LAX", 2, AddrMode.XInd, OpControl.Normal);
            Def(0xB3, "LAX", 2, AddrMode.IndY, OpControl.Normal);

            Def(0x87, "SAX", 2, AddrMode.Zp, OpControl.Normal);
            Def(0x97, "SAX", 2, AddrMode.ZpY, OpControl.Normal);
            Def(0x8F, "SAX", 3, AddrMode.Abs, OpControl.Normal);
            Def(0x83, "SAX", 2, AddrMode.XInd, OpControl.Normal);

            Def(0xC7, "DCP", 2, AddrMode.Zp, OpControl.Normal);
            Def(0xD7, "DCP", 2, AddrMode.ZpX, OpControl.Normal);
            Def(0xCF, "DCP", 3, AddrMode.Abs, OpControl.Normal);
            Def(0xDF, "DCP", 3, AddrMode.AbsX, OpControl.Normal);
            Def(0xDB, "DCP", 3, AddrMode.AbsY, OpControl.Normal);
            Def(0xC3, "DCP", 2, AddrMode.XInd, OpControl.Normal);
            Def(0xD3, "DCP", 2, AddrMode.IndY, OpControl.Normal);

            Def(0xE7, "ISB", 2, AddrMode.Zp, OpControl.Normal);
            Def(0xF7, "ISB", 2, AddrMode.ZpX, OpControl.Normal);
            Def(0xEF, "ISB", 3, AddrMode.Abs, OpControl.Normal);
            Def(0xFF, "ISB", 3, AddrMode.AbsX, OpControl.Normal);
            Def(0xFB, "ISB", 3, AddrMode.AbsY, OpControl.Normal);
            Def(0xE3, "ISB", 2, AddrMode.XInd, OpControl.Normal);
            Def(0xF3, "ISB", 2, AddrMode.IndY, OpControl.Normal);

            Def(0x07, "SLO", 2, AddrMode.Zp, OpControl.Normal);
            Def(0x17, "SLO", 2, AddrMode.ZpX, OpControl.Normal);
            Def(0x0F, "SLO", 3, AddrMode.Abs, OpControl.Normal);
            Def(0x1F, "SLO", 3, AddrMode.AbsX, OpControl.Normal);
            Def(0x1B, "SLO", 3, AddrMode.AbsY, OpControl.Normal);
            Def(0x03, "SLO", 2, AddrMode.XInd, OpControl.Normal);
            Def(0x13, "SLO", 2, AddrMode.IndY, OpControl.Normal);

            Def(0x27, "RLA", 2, AddrMode.Zp, OpControl.Normal);
            Def(0x37, "RLA", 2, AddrMode.ZpX, OpControl.Normal);
            Def(0x2F, "RLA", 3, AddrMode.Abs, OpControl.Normal);
            Def(0x3F, "RLA", 3, AddrMode.AbsX, OpControl.Normal);
            Def(0x3B, "RLA", 3, AddrMode.AbsY, OpControl.Normal);
            Def(0x23, "RLA", 2, AddrMode.XInd, OpControl.Normal);
            Def(0x33, "RLA", 2, AddrMode.IndY, OpControl.Normal);

            Def(0x47, "SRE", 2, AddrMode.Zp, OpControl.Normal);
            Def(0x57, "SRE", 2, AddrMode.ZpX, OpControl.Normal);
            Def(0x4F, "SRE", 3, AddrMode.Abs, OpControl.Normal);
            Def(0x5F, "SRE", 3, AddrMode.AbsX, OpControl.Normal);
            Def(0x5B, "SRE", 3, AddrMode.AbsY, OpControl.Normal);
            Def(0x43, "SRE", 2, AddrMode.XInd, OpControl.Normal);
            Def(0x53, "SRE", 2, AddrMode.IndY, OpControl.Normal);

            Def(0x67, "RRA", 2, AddrMode.Zp, OpControl.Normal);
            Def(0x77, "RRA", 2, AddrMode.ZpX, OpControl.Normal);
            Def(0x6F, "RRA", 3, AddrMode.Abs, OpControl.Normal);
            Def(0x7F, "RRA", 3, AddrMode.AbsX, OpControl.Normal);
            Def(0x7B, "RRA", 3, AddrMode.AbsY, OpControl.Normal);
            Def(0x63, "RRA", 2, AddrMode.XInd, OpControl.Normal);
            Def(0x73, "RRA", 2, AddrMode.IndY, OpControl.Normal);

            // Super Mario Bros может содержать эти illegal/NES-опкоды как данные или код.
            // Для статического лифтера пока делаем максимально безопасную заглушку.

            Def(0x12, "KIL", 1, AddrMode.Imp, OpControl.Normal);
            Def(0x92, "KIL", 1, AddrMode.Imp, OpControl.Normal);

            Def(0x93, "AXA", 2, AddrMode.IndY, OpControl.Normal);
            Def(0x9F, "AXA", 3, AddrMode.AbsY, OpControl.Normal);

            // === Еще немного unofficial 6502/NES opcodes ===

            // KIL / JAM / NOP-like invalid instructions
            Def(0x02, "KIL", 1, AddrMode.Imp, OpControl.Normal);
            Def(0x22, "KIL", 1, AddrMode.Imp, OpControl.Normal);
            Def(0x32, "KIL", 1, AddrMode.Imp, OpControl.Normal);
            Def(0x42, "KIL", 1, AddrMode.Imp, OpControl.Normal);
            Def(0x52, "KIL", 1, AddrMode.Imp, OpControl.Normal);
            Def(0x62, "KIL", 1, AddrMode.Imp, OpControl.Normal);
            Def(0x72, "KIL", 1, AddrMode.Imp, OpControl.Normal);
            Def(0xB2, "KIL", 1, AddrMode.Imp, OpControl.Normal);
            Def(0xD2, "KIL", 1, AddrMode.Imp, OpControl.Normal);
            Def(0xF2, "KIL", 1, AddrMode.Imp, OpControl.Normal);

            // Immediate unofficial
            Def(0x0B, "ANC", 2, AddrMode.Imm, OpControl.Normal);
            Def(0x2B, "ANC", 2, AddrMode.Imm, OpControl.Normal);
            Def(0x4B, "ALR", 2, AddrMode.Imm, OpControl.Normal);
            Def(0x6B, "ARR", 2, AddrMode.Imm, OpControl.Normal);
            Def(0x8B, "ANE", 2, AddrMode.Imm, OpControl.Normal);
            Def(0xAB, "LXA", 2, AddrMode.Imm, OpControl.Normal);
            Def(0xCB, "AXS", 2, AddrMode.Imm, OpControl.Normal);
            Def(0xEB, "SBC", 2, AddrMode.Imm, OpControl.Normal);

            // Memory unofficial
            Def(0x9C, "SHY", 3, AddrMode.AbsX, OpControl.Normal);
            Def(0x9E, "SHY", 3, AddrMode.AbsX, OpControl.Normal);
            Def(0xBB, "LAS", 3, AddrMode.AbsY, OpControl.Normal);
        }
    }

    public class Instruction
    {
        public ushort Address;
        public byte Opcode;
        public OpInfo Info;
        public int Length;
        public ushort Operand;
        public string Text;
        public OpControl Control;
        public ushort Target;
        public bool HasTarget;
        public ushort Fallthrough;
        public bool HasFallthrough;
    }

    public class AnalysisResult
    {
        public SortedDictionary<ushort, Instruction> Instructions = new SortedDictionary<ushort, Instruction>();
        public List<ushort> Labels = new List<ushort>();
        public List<ushort> Functions = new List<ushort>();
        public List<byte> UnknownOpcodes = new List<byte>();
        public List<ushort> IndirectJumps = new List<ushort>();
        public List<ushort> DynamicTargets = new List<ushort>();
        public ushort Entry;
    }

    public class Disassembler
    {
        NesRom _rom;
        Queue<ushort> _queue = new Queue<ushort>();
        Dictionary<ushort, bool> _seen = new Dictionary<ushort, bool>();
        AnalysisResult _result;
        public List<ushort> ForcedAddresses = new List<ushort>();

        public Disassembler(NesRom rom)
        {
            _rom = rom;
        }

        int GuessUnknownLength(byte op)
        {
            int low = op & 0x0F;

            switch (low)
            {
                case 0x00: return 1;
                case 0x01: return 2;
                case 0x02: return 1;
                case 0x03: return 2;
                case 0x04: return 2;
                case 0x05: return 2;
                case 0x06: return 2;
                case 0x07: return 2;
                case 0x08: return 1;
                case 0x09: return 2;
                case 0x0A: return 1;
                case 0x0B: return 2;
                case 0x0C: return 3;
                case 0x0D: return 3;
                case 0x0E: return 3;
                case 0x0F: return 3;
                default: return 1;
            }
        }

        public AnalysisResult Analyze()
        {
            _result = new AnalysisResult();
            _queue.Clear();
            _seen.Clear();

            // Reset vector: последние 4 байта PRG ROM (little-endian)
            ushort reset = _rom.ReadVector(0xFFFC);

            // Если вектор не указывает в PRG, пробуем начало PRG
            if (!IsValid(reset))
            {
                if (_rom.PrgRom.Length > 0)
                    reset = 0x8000;
                else
                    reset = 0;
            }

            _result.Entry = reset;

            if (IsValid(reset))
            {
                Enqueue(reset);
                if (!_result.Functions.Contains(reset))
                    _result.Functions.Add(reset);
            }

            // NMI и IRQ: добавляем ЦЕЛИ, а не сами адреса векторов
            ushort nmiTarget = _rom.ReadVector(0xFFFA);
            ushort irqTarget = _rom.ReadVector(0xFFFE);

            if (IsValid(nmiTarget))
            {
                Enqueue(nmiTarget);
                if (!_result.Functions.Contains(nmiTarget))
                    _result.Functions.Add(nmiTarget);
            }

            if (IsValid(irqTarget))
            {
                Enqueue(irqTarget);
                if (!_result.Functions.Contains(irqTarget))
                    _result.Functions.Add(irqTarget);
            }

            foreach (ushort forced in ForcedAddresses)
            {
                if (IsValid(forced))
                {
                    Enqueue(forced);

                    if (!_result.Functions.Contains(forced))
                        _result.Functions.Add(forced);

                    if (!_result.DynamicTargets.Contains(forced))
                        _result.DynamicTargets.Add(forced);
                }
            }

            while (_queue.Count > 0)
            {
                ushort addr = _queue.Dequeue();
                if (_result.Instructions.ContainsKey(addr)) continue;

                int off = _rom.AddrToOffset(addr);
                if (off < 0) continue;

                Instruction inst = Decode(addr, off);
                _result.Instructions.Add(addr, inst);

                if (inst.Control == OpControl.Invalid && !_result.UnknownOpcodes.Contains(inst.Opcode))
                    _result.UnknownOpcodes.Add(inst.Opcode);

                if (inst.Control == OpControl.JmpInd && !_result.IndirectJumps.Contains(addr))
                    _result.IndirectJumps.Add(addr);

                if (inst.Control == OpControl.JmpInd && inst.HasTarget && !_result.DynamicTargets.Contains(inst.Target))
                    _result.DynamicTargets.Add(inst.Target);

                if ((inst.Control == OpControl.Jsr || inst.Control == OpControl.Jmp) &&
                    inst.HasTarget && !_result.Functions.Contains(inst.Target))
                {
                    _result.Functions.Add(inst.Target);
                }

                if (inst.HasTarget) Enqueue(inst.Target);
                if (inst.HasFallthrough) Enqueue(inst.Fallthrough);
            }

            // Remove any accidentally decoded vector-area addresses.
            List<ushort> bad = new List<ushort>();

            foreach (ushort a in _result.Instructions.Keys)
            {
                if (a < 0x8000 || a >= 0xFFFA)
                    bad.Add(a);
            }

            foreach (ushort a in bad)
            {
                _result.Instructions.Remove(a);
            }

            _result.Labels = new List<ushort>(_result.Instructions.Keys);
            _result.Labels.Sort();

            _result.Labels.RemoveAll(delegate (ushort a)
            {
                return a < 0x8000 || a >= 0xFFFA;
            });
            return _result;
        }

        bool IsValid(ushort addr)
        {
            if (addr < 0x8000) return false;

            // Do not treat CPU vector area as executable code.
            if (addr >= 0xFFFA) return false;

            return _rom.AddrToOffset(addr) >= 0;
        }

        void Enqueue(ushort addr)
        {
            if (!IsValid(addr)) return;
            if (_seen.ContainsKey(addr)) return;
            _seen.Add(addr, true);
            _queue.Enqueue(addr);
        }

        Instruction Decode(ushort addr, int offset)
        {
            Instruction inst = new Instruction();
            inst.Address = addr;

            byte[] prg = _rom.PrgRom;
            byte op = prg[offset];
            inst.Opcode = op;

            OpInfo info = Cpu6502.Table[op];
            if (info == null)
            {
                inst.Length = GuessUnknownLength(op);
                inst.Control = OpControl.Invalid;
                inst.Text = "??? $" + op.ToString("X2");
                inst.Fallthrough = (ushort)(addr + inst.Length);
                inst.HasFallthrough = IsValid(inst.Fallthrough);
                return inst;
            }

            if (offset + info.Len > prg.Length)
            {
                inst.Length = 1;
                inst.Control = OpControl.Invalid;
                inst.Info = info;
                inst.Text = info.Mn + " <truncated>";
                inst.Fallthrough = (ushort)(addr + 1);
                inst.HasFallthrough = IsValid(inst.Fallthrough);
                return inst;
            }

            inst.Info = info;
            inst.Length = info.Len;
            inst.Control = info.Ctrl;

            if (inst.Length >= 2)
                inst.Operand = prg[offset + 1];

            if (inst.Length == 3)
                inst.Operand |= (ushort)(prg[offset + 2] << 8);

            inst.Text = Format(inst);

            switch (info.Ctrl)
            {
                case OpControl.Branch:
                    {
                        sbyte rel = (sbyte)inst.Operand;
                        inst.Target = (ushort)(addr + 2 + rel);
                        inst.HasTarget = IsValid(inst.Target);
                        inst.Fallthrough = (ushort)(addr + 2);
                        inst.HasFallthrough = IsValid(inst.Fallthrough);
                        break;
                    }

                case OpControl.Jmp:
                    {
                        inst.Target = inst.Operand;
                        inst.HasTarget = IsValid(inst.Target);
                        break;
                    }

                case OpControl.JmpInd:
                    {
                        int ptrOff = _rom.AddrToOffset(inst.Operand);
                        if (ptrOff >= 0 && ptrOff + 1 < prg.Length)
                        {
                            ushort possible = (ushort)(prg[ptrOff] | (prg[ptrOff + 1] << 8));
                            if (IsValid(possible))
                            {
                                inst.Target = possible;
                                inst.HasTarget = true;
                            }
                        }
                        break;
                    }

                case OpControl.Jsr:
                    {
                        inst.Target = inst.Operand;
                        inst.HasTarget = IsValid(inst.Target);
                        inst.Fallthrough = (ushort)(addr + 3);
                        inst.HasFallthrough = IsValid(inst.Fallthrough);
                        break;
                    }

                case OpControl.Rts:
                case OpControl.Rti:
                case OpControl.Brk:
                    break;

                default:
                    {
                        inst.Fallthrough = (ushort)(addr + inst.Length);
                        inst.HasFallthrough = IsValid(inst.Fallthrough);
                        break;
                    }
            }

            return inst;
        }

        string Format(Instruction inst)
        {
            if (inst.Info == null)
                return "??? $" + inst.Opcode.ToString("X2");

            string mn = inst.Info.Mn.PadRight(3, ' ');
            string op = string.Empty;

            switch (inst.Info.Mode)
            {
                case AddrMode.Imp: op = string.Empty; break;
                case AddrMode.Acc: op = "A"; break;
                case AddrMode.Imm: op = "#$" + inst.Operand.ToString("X2"); break;
                case AddrMode.Zp: op = "$" + inst.Operand.ToString("X2"); break;
                case AddrMode.ZpX: op = "$" + inst.Operand.ToString("X2") + ",X"; break;
                case AddrMode.ZpY: op = "$" + inst.Operand.ToString("X2") + ",Y"; break;
                case AddrMode.Abs: op = "$" + inst.Operand.ToString("X4"); break;
                case AddrMode.AbsX: op = "$" + inst.Operand.ToString("X4") + ",X"; break;
                case AddrMode.AbsY: op = "$" + inst.Operand.ToString("X4") + ",Y"; break;
                case AddrMode.Ind: op = "($" + inst.Operand.ToString("X4") + ")"; break;
                case AddrMode.XInd: op = "($" + inst.Operand.ToString("X2") + ",X)"; break;
                case AddrMode.IndY: op = "($" + inst.Operand.ToString("X2") + "),Y"; break;
                case AddrMode.Rel:
                    {
                        sbyte rel = (sbyte)inst.Operand;
                        ushort target = (ushort)(inst.Address + 2 + rel);
                        op = "$" + target.ToString("X4");
                        break;
                    }
            }

            return (mn + " " + op).TrimEnd();
        }
    }

    public class Lifter
    {
        NesRom _rom;
        AnalysisResult _model;
        string _nsName;
        Dictionary<ushort, bool> _labels = new Dictionary<ushort, bool>();
        List<ushort> _emitted = new List<ushort>();
        ushort _nmiTarget;
        bool _hasNmi;
        List<ushort> _dispatch = new List<ushort>();

        public Lifter(NesRom rom, AnalysisResult model, string gameName)
        {
            _rom = rom;
            _model = model;
            _nsName = FileSystemUtil.SanitizeIdentifier(gameName);

            _labels = new Dictionary<ushort, bool>();

            foreach (ushort a in _model.Labels)
            {
                if (IsEmittableLabel(a))
                    _labels[a] = true;
            }

            if (IsEmittableLabel(_model.Entry))
                _labels[_model.Entry] = true;

            _emitted = new List<ushort>(_labels.Keys);
            _emitted.Sort();

            List<ushort> dispatch = new List<ushort>();

            // Функции / точки входа
            foreach (ushort a in _model.Functions)
            {
                if (_labels.ContainsKey(a) && !dispatch.Contains(a))
                    dispatch.Add(a);
            }

            // Известные dynamic targets из forced/dynamic_targets
            foreach (ushort a in _model.DynamicTargets)
            {
                if (_labels.ContainsKey(a) && !dispatch.Contains(a))
                    dispatch.Add(a);
            }

            // Return-адреса после JSR
            // А также статические JMP/JMP indirect targets
            foreach (Instruction inst in _model.Instructions.Values)
            {
                if (inst.Control == OpControl.Jsr &&
                    inst.HasFallthrough &&
                    _labels.ContainsKey(inst.Fallthrough) &&
                    !dispatch.Contains(inst.Fallthrough))
                {
                    dispatch.Add(inst.Fallthrough);
                }

                if ((inst.Control == OpControl.Jmp || inst.Control == OpControl.JmpInd) &&
                    inst.HasTarget &&
                    _labels.ContainsKey(inst.Target) &&
                    !dispatch.Contains(inst.Target))
                {
                    dispatch.Add(inst.Target);
                }
            }

            // Если совсем пусто, оставляем хотя бы entry
            if (dispatch.Count == 0 && _model.Entry != 0 && _labels.ContainsKey(_model.Entry))
                dispatch.Add(_model.Entry);

            dispatch.Sort();
            _dispatch = dispatch;

            _nmiTarget = _rom.ReadVector(0xFFFA);
            _hasNmi = IsEmittableLabel(_nmiTarget) && _labels.ContainsKey(_nmiTarget);
        }

        bool IsEmittableLabel(ushort addr)
        {
            if (addr < 0x8000) return false;

            // CPU vectors: NMI/RESET/IRQ are data, not code.
            // $FFFA/$FFFB = NMI
            // $FFFC/$FFFD = RESET
            // $FFFE/$FFFF = IRQ
            if (addr >= 0xFFFA) return false;

            return _model.Instructions.ContainsKey(addr);
        }

        public string Generate()
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("// <auto-generated>");
            sb.AppendLine("// NES static recompiler generated code.");
            sb.AppendLine("// Runtime: .NET Framework. PPU/APU/mapper are stubbed.");
            sb.AppendLine("// </auto-generated>");
            sb.AppendLine();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Drawing;");
            sb.AppendLine("using System.Windows.Forms;");
            sb.AppendLine("using System.Threading;");
            sb.AppendLine("using System.Drawing.Imaging;");
            sb.AppendLine("using System.Runtime.InteropServices;");
            sb.AppendLine();
            sb.AppendLine("namespace Lifted." + _nsName);
            sb.AppendLine("{");

            AppendProgram(sb);
            AppendRuntime(sb);
            AppendMemory(sb);
            AppendPpu(sb);
            AppendApu(sb);
            AppendMapper(sb);
            AppendAudio(sb);
            AppendForm(sb);

            sb.AppendLine("}");
            return sb.ToString();
        }

        void AppendProgram(StringBuilder sb)
        {
            sb.AppendLine("static class Program");
            sb.AppendLine("{");
            sb.AppendLine("[STAThread]");
            sb.AppendLine("static void Main()");
            sb.AppendLine("{");
            sb.AppendLine("try");
            sb.AppendLine("{");
            sb.AppendLine("Application.EnableVisualStyles();");
            sb.AppendLine("Runtime.Init();");
            sb.AppendLine("PpuForm form = new PpuForm();");
            sb.AppendLine("Thread cpuThread = new Thread(new ThreadStart(Runtime.CpuThread));");
            sb.AppendLine("cpuThread.IsBackground = true;");
            sb.AppendLine("cpuThread.Priority = ThreadPriority.BelowNormal;");
            sb.AppendLine("cpuThread.Start();");
            sb.AppendLine("Application.Run(form);");
            sb.AppendLine("}");
            sb.AppendLine("catch (Exception ex)");
            sb.AppendLine("{");
            sb.AppendLine("try { System.IO.File.WriteAllText(System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, \"fatal.log\"), ex.ToString()); } catch { }");
            sb.AppendLine("MessageBox.Show(ex.ToString(), \"NES Lifted fatal\", MessageBoxButtons.OK, MessageBoxIcon.Error);");
            sb.AppendLine("}");
            sb.AppendLine("}");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        void AppendRuntime(StringBuilder sb)
        {
            sb.AppendLine("static class Runtime");
            sb.AppendLine("{");

            sb.AppendLine("public static string GameName = \"" + _nsName + "\";");
            sb.AppendLine("public static int Mapper = " + _rom.Mapper + ";");

            int mir = 0;
            if (_rom.Mirroring == "Vertical")
                mir = 1;
            else if (_rom.Mirroring == "Four-screen")
                mir = 2;

            sb.AppendLine("public static int Mirroring = " + mir + ";");

            AppendByteArrayField(sb, "PrgRom", _rom.PrgRom);
            AppendByteArrayField(sb, "ChrRom", _rom.ChrRom);

            sb.AppendLine("public static byte[] Ram = new byte[0x800];");
            sb.AppendLine("public static byte[] SaveRam = new byte[0x2000];");
            sb.AppendLine("public static byte A;");
            sb.AppendLine("public static byte X;");
            sb.AppendLine("public static byte Y;");
            sb.AppendLine("public static byte SP;");
            sb.AppendLine("public static byte P;");
            sb.AppendLine("public static string LastError;");
            sb.AppendLine("public static long InsCount;");
            sb.AppendLine("public static ushort LastPC;");
            sb.AppendLine("public static ushort DispatchTarget;");
            sb.AppendLine("public static volatile string CpuState;");
            sb.AppendLine("public static volatile int TrapCount;");
            sb.AppendLine("public static string LastTrap;");
            sb.AppendLine("public static object DynLock = new object();");
            sb.AppendLine("public static System.Collections.Generic.Dictionary<ushort, bool> SeenDynamic = new System.Collections.Generic.Dictionary<ushort, bool>();");
            sb.AppendLine("public static volatile bool NmiPending;");
            sb.AppendLine("public static volatile bool InNmi;");
            sb.AppendLine("public static ushort NmiVector = 0x" + _nmiTarget.ToString("X4") + ";");
            sb.AppendLine("public static object FrameLock = new object();");
            sb.AppendLine("public static byte Joy1Buttons;");
            sb.AppendLine("public static byte Joy2Buttons;");
            sb.AppendLine("public static byte Joy1Latch;");
            sb.AppendLine("public static byte Joy2Latch;");
            sb.AppendLine("public static int Joy1Idx;");
            sb.AppendLine("public static int Joy2Idx;");
            sb.AppendLine();

            sb.AppendLine("public static void Init()");
            sb.AppendLine("{");
            sb.AppendLine("A = 0; X = 0; Y = 0; SP = 0xFD; P = 0x24; DispatchTarget = 0; CpuState = \"Init\";");
            sb.AppendLine("Memory.Reset(); Ppu.Reset(); Apu.Reset(); MapperStub.Reset();");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("public static void CpuThread()");
            sb.AppendLine("{");
            sb.AppendLine("try { CpuState = \"Starting\"; Reset(); CpuState = \"EnteringRun\"; Run(); CpuState = LastError != null ? \"Trap\" : \"Exited\"; }");
            sb.AppendLine("catch (Exception ex) { CpuState = \"Exception\"; OnCpuException(ex); }");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("public static void Reset()");
            sb.AppendLine("{");
            sb.AppendLine("A = 0; X = 0; Y = 0; SP = 0xFD; P = 0x24; LastError = null; LastTrap = null; InsCount = 0; LastPC = 0; TrapCount = 0; DispatchTarget = 0; CpuState = \"Reset\";");
            sb.AppendLine("if (SeenDynamic != null) SeenDynamic.Clear();");
            sb.AppendLine("}");
            sb.AppendLine();

            AppendRun(sb);
            AppendRuntimeHelpers(sb);
            AppendInterpreter(sb);
            sb.AppendLine("}");
            sb.AppendLine();
        }

        void AppendRun(StringBuilder sb)
        {
            bool hasEntry = _model.Entry != 0 && _labels.ContainsKey(_model.Entry);

            sb.AppendLine("public static void Run()");
            sb.AppendLine("{");
            sb.AppendLine("try");
            sb.AppendLine("{");
            Line(sb, "Runtime.CpuState = \"Running\";");

            if (hasEntry)
                Line(sb, "goto L" + _model.Entry.ToString("X4") + ";");
            else
                Line(sb, "Runtime.Trap(\"No valid entry/reset vector.\"); return;");

            if (_emitted.Count > 0)
            {
                sb.AppendLine("Dispatch:");
                sb.AppendLine("{");
                Line(sb, "switch (Runtime.DispatchTarget)");
                Line(sb, "{");
                foreach (ushort daddr in _emitted)
                    Line(sb, "case 0x" + daddr.ToString("X4") + ": goto L" + daddr.ToString("X4") + ";");
                Line(sb, "default: Runtime.ReportDynamicTarget(Runtime.DispatchTarget); if (!Runtime.Interpret()) { Runtime.Trap(\"Dynamic dispatch -> $\" + Runtime.DispatchTarget.ToString(\"X4\")); return; } goto Dispatch;");
                Line(sb, "}");
                sb.AppendLine("}");
            }

            foreach (ushort addr in _emitted)
            {
                sb.AppendLine("L" + addr.ToString("X4") + ":");
                sb.AppendLine("{");

                Line(sb, "Runtime.LastPC = 0x" + addr.ToString("X4") + ";");
                Line(sb, "Runtime.InsCount++;");

                if (_hasNmi)
                {
                    Line(sb, "if (Runtime.CheckNmi(0x" + addr.ToString("X4") + ")) goto L" + _nmiTarget.ToString("X4") + ";");
                }

                Instruction inst;
                if (_model.Instructions.TryGetValue(addr, out inst))
                {
                    Line(sb, "// " + inst.Text);
                    EmitInstruction(sb, inst);
                }
                else
                {
                    Line(sb, "Runtime.Trap(\"Label without instruction.\"); return;");
                }

                sb.AppendLine("}");
            }

            sb.AppendLine("}");
            sb.AppendLine("catch (Exception ex) { OnCpuException(ex); }");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        void AppendRuntimeHelpers(StringBuilder sb)
        {
            sb.AppendLine("public static void SetCarry(bool v) { P = (byte)(v ? (P | 0x01) : (P & 0xFE)); }");
            sb.AppendLine("public static void SetZero(bool v) { P = (byte)(v ? (P | 0x02) : (P & 0xFD)); }");
            sb.AppendLine("public static void SetInterrupt(bool v) { P = (byte)(v ? (P | 0x04) : (P & 0xFB)); }");
            sb.AppendLine("public static void SetDecimal(bool v) { P = (byte)(v ? (P | 0x08) : (P & 0xF7)); }");
            sb.AppendLine("public static void SetOverflow(bool v) { P = (byte)(v ? (P | 0x40) : (P & 0xBF)); }");
            sb.AppendLine("public static void SetNegative(bool v) { P = (byte)(v ? (P | 0x80) : (P & 0x7F)); }");
            sb.AppendLine("public static void SetNZ(byte v) { P = (byte)((P & 0x7D) | (v == 0 ? 0x02 : 0) | (v & 0x80)); }");
            sb.AppendLine();

            sb.AppendLine("public static byte Asl(byte v) { SetCarry((v & 0x80) != 0); v = (byte)(v << 1); SetNZ(v); return v; }");
            sb.AppendLine("public static byte Lsr(byte v) { SetCarry((v & 0x01) != 0); v = (byte)(v >> 1); SetNZ(v); return v; }");
            sb.AppendLine("public static byte Rol(byte v) { int c = (P & 0x01); SetCarry((v & 0x80) != 0); v = (byte)((v << 1) | c); SetNZ(v); return v; }");
            sb.AppendLine("public static byte Ror(byte v) { int c = (P & 0x01); SetCarry((v & 0x01) != 0); v = (byte)((v >> 1) | (c << 7)); SetNZ(v); return v; }");
            sb.AppendLine();

            sb.AppendLine("public static void Adc(byte v)");
            sb.AppendLine("{");
            sb.AppendLine("int aa = A; int vv = v; int c = (P & 0x01); int sum = aa + vv + c;");
            sb.AppendLine("SetOverflow(((~(aa ^ vv) & (aa ^ sum)) & 0x80) != 0);");
            sb.AppendLine("SetCarry(sum > 0xFF);");
            sb.AppendLine("A = (byte)sum; SetNZ(A);");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("public static void Sbc(byte v)");
            sb.AppendLine("{");
            sb.AppendLine("int aa = A; int vv = v; int c = (P & 0x01); int diff = aa - vv - (1 - c);");
            sb.AppendLine("SetOverflow((((aa ^ vv) & (aa ^ diff)) & 0x80) != 0);");
            sb.AppendLine("SetCarry(diff >= 0);");
            sb.AppendLine("A = (byte)diff; SetNZ(A);");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("public static void Compare(byte reg, byte v) { int diff = reg - v; SetCarry(reg >= v); SetNZ((byte)diff); }");
            sb.AppendLine("public static void Bit(byte v) { SetZero((A & v) == 0); SetOverflow((v & 0x40) != 0); SetNegative((v & 0x80) != 0); }");
            sb.AppendLine();

            sb.AppendLine("public static ushort ReadPtrZp(byte ptr)");
            sb.AppendLine("{");
            sb.AppendLine("byte lo = Memory.Read(ptr);");
            sb.AppendLine("byte hi = Memory.Read((byte)(ptr + 1));");
            sb.AppendLine("return (ushort)(lo | (hi << 8));");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("public static ushort AddrXInd(byte op) { return ReadPtrZp((byte)(op + X)); }");
            sb.AppendLine("public static ushort AddrIndY(byte op) { return (ushort)(ReadPtrZp(op) + Y); }");
            sb.AppendLine();

            sb.AppendLine("public static void Push(byte v) { Memory.Write((ushort)(0x100 + SP), v); SP = (byte)(SP - 1); }");
            sb.AppendLine("public static byte Pull() { SP = (byte)(SP + 1); return Memory.Read((ushort)(0x100 + SP)); }");
            sb.AppendLine("public static void Push16(ushort v) { Push((byte)(v >> 8)); Push((byte)(v & 0xFF)); }");
            sb.AppendLine("public static ushort Pull16() { byte lo = Pull(); byte hi = Pull(); return (ushort)(lo | (hi << 8)); }");
            sb.AppendLine("public static void JsrPush(ushort returnAddress) { Push16((ushort)(returnAddress - 1)); }");
            sb.AppendLine("public static ushort RtsPull() { return (ushort)(Pull16() + 1); }");
            sb.AppendLine("public static void JoyStrobe(byte v) { if ((v & 1) != 0) { Joy1Latch = Joy1Buttons; Joy2Latch = Joy2Buttons; Joy1Idx = 0; Joy2Idx = 0; } }");
            sb.AppendLine("public static byte JoyRead1() { int i = Joy1Idx; Joy1Idx = i + 1; if (i < 8) return (byte)((Joy1Latch >> i) & 1); return 0; }");
            sb.AppendLine("public static byte JoyRead2() { int i = Joy2Idx; Joy2Idx = i + 1; if (i < 8) return (byte)((Joy2Latch >> i) & 1); return 0; }");
            sb.AppendLine();
            sb.AppendLine("public static bool CheckNmi(ushort pc)");
            sb.AppendLine("{");
            sb.AppendLine("    if (!NmiPending || InNmi || (Ppu.Ctrl & 0x80) == 0)");
            sb.AppendLine("    {");
            sb.AppendLine("        if ((Ppu.Ctrl & 0x80) == 0) NmiPending = false;");
            sb.AppendLine("        return false;");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    NmiPending = false;");
            sb.AppendLine("    InNmi = true;");
            sb.AppendLine();
            sb.AppendLine("    // NMI pushes PC and P, then sets I flag.");
            sb.AppendLine("    Push16(pc);");
            sb.AppendLine("    Push((byte)((P & 0xEF) | 0x20));");
            sb.AppendLine("    P = (byte)(P | 0x04);");
            sb.AppendLine();
            sb.AppendLine("    return true;");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("public static void UnknownOpcode(byte op, ushort addr)");
            sb.AppendLine("{");
            sb.AppendLine("    string msg = string.Format(\"Unknown opcode ${0:X2} at ${1:X4}\", op, addr);");
            sb.AppendLine("    SetTrap(msg);");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("public static void Trap(string message)");
            sb.AppendLine("{");
            sb.AppendLine("    SetTrap(message);");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("public static void OnCpuException(Exception ex)");
            sb.AppendLine("{");
            sb.AppendLine("    SetTrap(\"CPU exception: \" + ex.ToString());");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("public static void SetTrap(string message)");
            sb.AppendLine("{");
            sb.AppendLine("    LastError = message;");
            sb.AppendLine("    CpuState = \"Trap\";");
            sb.AppendLine();
            sb.AppendLine("    if (LastTrap != message)");
            sb.AppendLine("    {");
            sb.AppendLine("        LastTrap = message;");
            sb.AppendLine("        TrapCount++;");
            sb.AppendLine();
            sb.AppendLine("        Console.WriteLine(message);");
            sb.AppendLine();
            sb.AppendLine("        try");
            sb.AppendLine("        {");
            sb.AppendLine("            System.IO.File.AppendAllText(");
            sb.AppendLine("                System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, \"cpu_trap.log\"),");
            sb.AppendLine("                DateTime.Now.ToString(\"s\") + \" \" + message + Environment.NewLine);");
            sb.AppendLine("        }");
            sb.AppendLine("        catch { }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("public static void ReportDynamicTarget(ushort addr)");
            sb.AppendLine("{");
            sb.AppendLine("    if (addr < 0x8000 || addr >= 0xFFFA) return;");
            sb.AppendLine();
            sb.AppendLine("    lock (DynLock)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (!SeenDynamic.ContainsKey(addr))");
            sb.AppendLine("        {");
            sb.AppendLine("            SeenDynamic.Add(addr, true);");
            sb.AppendLine();
            sb.AppendLine("            try");
            sb.AppendLine("            {");
            sb.AppendLine("                string path = System.IO.Path.Combine(");
            sb.AppendLine("                    System.AppDomain.CurrentDomain.BaseDirectory,");
            sb.AppendLine("                    \"dynamic_targets.log\");");
            sb.AppendLine();
            sb.AppendLine("                System.IO.File.AppendAllText(");
            sb.AppendLine("                    path,");
            sb.AppendLine("                    addr.ToString(\"X4\") + System.Environment.NewLine);");
            sb.AppendLine("            }");
            sb.AppendLine("            catch { }");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        void AppendInterpreter(StringBuilder sb)
        {
            // --- таблицы опкодов, сгенерированные из Cpu6502.Table ---
            sb.Append("public static readonly byte[] OpLen = new byte[256]{");
            for (int i = 0; i < 256; i++)
            {
                if (i > 0) sb.Append(",");
                OpInfo oi = Cpu6502.Table[i];
                int len = (oi == null) ? 1 : oi.Len;
                sb.Append(len.ToString(CultureInfo.InvariantCulture));
            }
            sb.AppendLine("};");

            sb.Append("public static readonly byte[] OpMnem = new byte[256]{");
            for (int i = 0; i < 256; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append(MnemIdFor(Cpu6502.Table[i]).ToString(CultureInfo.InvariantCulture));
            }
            sb.AppendLine("};");

            sb.Append("public static readonly byte[] OpMode = new byte[256]{");
            for (int i = 0; i < 256; i++)
            {
                if (i > 0) sb.Append(",");
                OpInfo oi = Cpu6502.Table[i];
                int m = (oi == null) ? 0 : (int)oi.Mode;
                sb.Append(m.ToString(CultureInfo.InvariantCulture));
            }
            sb.AppendLine("};");

            // --- предикат: адрес ли это статически эмитированного кода ---
            sb.AppendLine("public static bool IsStaticLabel(ushort a)");
            sb.AppendLine("{");
            sb.AppendLine("switch (a) {");
            foreach (ushort a in _emitted)
                sb.AppendLine("case 0x" + a.ToString("X4") + ": return true;");
            sb.AppendLine("default: return false;");
            sb.AppendLine("}");
            sb.AppendLine("}");

            // --- чтение операнда по режиму адресации ---
            sb.AppendLine("static byte ReadOp(byte mode, ushort pc, byte imm)");
            sb.AppendLine("{");
            sb.AppendLine("switch (mode) {");
            sb.AppendLine("case 1: return A;");
            sb.AppendLine("case 2: return imm;");
            sb.AppendLine("case 3: return Memory.Read(imm);");
            sb.AppendLine("case 4: return Memory.Read((byte)(imm + X));");
            sb.AppendLine("case 5: return Memory.Read((byte)(imm + Y));");
            sb.AppendLine("case 6: return Memory.Read(Memory.Read16((ushort)(pc + 1)));");
            sb.AppendLine("case 7: return Memory.Read(unchecked((ushort)(Memory.Read16((ushort)(pc + 1)) + X)));");
            sb.AppendLine("case 8: return Memory.Read(unchecked((ushort)(Memory.Read16((ushort)(pc + 1)) + Y)));");
            sb.AppendLine("case 10: return Memory.Read(AddrXInd(imm));");
            sb.AppendLine("case 11: return Memory.Read(AddrIndY(imm));");
            sb.AppendLine("default: return 0;");
            sb.AppendLine("}");
            sb.AppendLine("}");

            // --- вычисление адреса для store / read-modify-write ---
            sb.AppendLine("static ushort AddrOp(byte mode, ushort pc, byte imm)");
            sb.AppendLine("{");
            sb.AppendLine("switch (mode) {");
            sb.AppendLine("case 3: return imm;");
            sb.AppendLine("case 4: return (byte)(imm + X);");
            sb.AppendLine("case 5: return (byte)(imm + Y);");
            sb.AppendLine("case 6: return Memory.Read16((ushort)(pc + 1));");
            sb.AppendLine("case 7: return unchecked((ushort)(Memory.Read16((ushort)(pc + 1)) + X));");
            sb.AppendLine("case 8: return unchecked((ushort)(Memory.Read16((ushort)(pc + 1)) + Y));");
            sb.AppendLine("case 10: return AddrXInd(imm);");
            sb.AppendLine("case 11: return AddrIndY(imm);");
            sb.AppendLine("default: return 0;");
            sb.AppendLine("}");
            sb.AppendLine("}");

            // --- сам интерпретатор: крутится, пока PC динамический ---
            sb.AppendLine("public static bool Interpret()");
            sb.AppendLine("{");
            sb.AppendLine("ushort pc = DispatchTarget;");
            sb.AppendLine("for (int g = 0; g < 4000000; g++)");
            sb.AppendLine("{");
            // NMI имеет приоритет
            sb.AppendLine("if (CheckNmi(pc)) { DispatchTarget = NmiVector; return true; }");
            // вернулись в статический код -> выходим в Dispatch
            sb.AppendLine("if (IsStaticLabel(pc)) { DispatchTarget = pc; return true; }");
            sb.AppendLine("LastPC = pc; InsCount++;");
            sb.AppendLine("byte op = Memory.Read(pc);");
            sb.AppendLine("byte mnem = OpMnem[op];");
            sb.AppendLine("byte mode = OpMode[op];");
            sb.AppendLine("byte len = OpLen[op];");
            sb.AppendLine("byte imm = (len >= 2) ? Memory.Read((ushort)(pc + 1)) : (byte)0;");

            // поток управления (переходы) обрабатываем до общего switch
            sb.AppendLine("switch (mnem) {");
            // JMP abs / JMP (ind)
            sb.AppendLine("case 44: { ushort tgt = (mode == 9) ? Memory.Read16(Memory.Read16((ushort)(pc + 1))) : Memory.Read16((ushort)(pc + 1)); if (IsStaticLabel(tgt)) { DispatchTarget = tgt; return true; } pc = tgt; continue; }");
            // JSR
            sb.AppendLine("case 45: { JsrPush((ushort)(pc + 3)); ushort tgt = Memory.Read16((ushort)(pc + 1)); if (IsStaticLabel(tgt)) { DispatchTarget = tgt; return true; } pc = tgt; continue; }");
            // RTS
            sb.AppendLine("case 46: { ushort tgt = RtsPull(); if (IsStaticLabel(tgt)) { DispatchTarget = tgt; return true; } pc = tgt; continue; }");
            // RTI
            sb.AppendLine("case 47: { P = (byte)((Pull() & 0xEF) | 0x20); ushort tgt = Pull16(); InNmi = false; if (IsStaticLabel(tgt)) { DispatchTarget = tgt; return true; } pc = tgt; continue; }");
            // BRK
            sb.AppendLine("case 48: { Push16((ushort)(pc + 2)); Push((byte)(P | 0x30)); P = (byte)(P | 0x04); ushort tgt = Memory.Read16(0xFFFE); if (IsStaticLabel(tgt)) { DispatchTarget = tgt; return true; } pc = tgt; continue; }");
            // Branch
            sb.AppendLine("case 49: { bool c; switch (op) { case 0x10: c = (P & 0x80) == 0; break; case 0x30: c = (P & 0x80) != 0; break; case 0x50: c = (P & 0x40) == 0; break; case 0x70: c = (P & 0x40) != 0; break; case 0x90: c = (P & 0x01) == 0; break; case 0xB0: c = (P & 0x01) != 0; break; case 0xD0: c = (P & 0x02) == 0; break; case 0xF0: c = (P & 0x02) != 0; break; default: c = false; break; } ushort nxt = c ? (ushort)(pc + 2 + (sbyte)imm) : (ushort)(pc + 2); if (IsStaticLabel(nxt)) { DispatchTarget = nxt; return true; } pc = nxt; continue; }");
            sb.AppendLine("}");

            // обычные инструкции
            sb.AppendLine("byte val = ReadOp(mode, pc, imm);");
            sb.AppendLine("switch (mnem) {");
            sb.AppendLine("case 1: A = val; SetNZ(A); break;");
            sb.AppendLine("case 2: X = val; SetNZ(X); break;");
            sb.AppendLine("case 3: Y = val; SetNZ(Y); break;");
            sb.AppendLine("case 4: Memory.Write(AddrOp(mode, pc, imm), A); break;");
            sb.AppendLine("case 5: Memory.Write(AddrOp(mode, pc, imm), X); break;");
            sb.AppendLine("case 6: Memory.Write(AddrOp(mode, pc, imm), Y); break;");
            sb.AppendLine("case 7: X = A; SetNZ(X); break;");
            sb.AppendLine("case 8: Y = A; SetNZ(Y); break;");
            sb.AppendLine("case 9: A = X; SetNZ(A); break;");
            sb.AppendLine("case 10: A = Y; SetNZ(A); break;");
            sb.AppendLine("case 11: SP = X; break;");
            sb.AppendLine("case 12: X = SP; SetNZ(X); break;");
            sb.AppendLine("case 13: X = (byte)(X + 1); SetNZ(X); break;");
            sb.AppendLine("case 14: Y = (byte)(Y + 1); SetNZ(Y); break;");
            sb.AppendLine("case 15: X = (byte)(X - 1); SetNZ(X); break;");
            sb.AppendLine("case 16: Y = (byte)(Y - 1); SetNZ(Y); break;");
            sb.AppendLine("case 17: Adc(val); break;");
            sb.AppendLine("case 18: Sbc(val); break;");
            sb.AppendLine("case 19: Compare(A, val); break;");
            sb.AppendLine("case 20: Compare(X, val); break;");
            sb.AppendLine("case 21: Compare(Y, val); break;");
            sb.AppendLine("case 22: A = (byte)(A & val); SetNZ(A); break;");
            sb.AppendLine("case 23: A = (byte)(A | val); SetNZ(A); break;");
            sb.AppendLine("case 24: A = (byte)(A ^ val); SetNZ(A); break;");
            sb.AppendLine("case 25: Bit(val); break;");
            sb.AppendLine("case 26: if (mode == 1) A = Asl(A); else { ushort a = AddrOp(mode, pc, imm); Memory.Write(a, Asl(Memory.Read(a))); } break;");
            sb.AppendLine("case 27: if (mode == 1) A = Lsr(A); else { ushort a = AddrOp(mode, pc, imm); Memory.Write(a, Lsr(Memory.Read(a))); } break;");
            sb.AppendLine("case 28: if (mode == 1) A = Rol(A); else { ushort a = AddrOp(mode, pc, imm); Memory.Write(a, Rol(Memory.Read(a))); } break;");
            sb.AppendLine("case 29: if (mode == 1) A = Ror(A); else { ushort a = AddrOp(mode, pc, imm); Memory.Write(a, Ror(Memory.Read(a))); } break;");
            sb.AppendLine("case 30: { ushort a = AddrOp(mode, pc, imm); byte t = (byte)(Memory.Read(a) + 1); SetNZ(t); Memory.Write(a, t); } break;");
            sb.AppendLine("case 31: { ushort a = AddrOp(mode, pc, imm); byte t = (byte)(Memory.Read(a) - 1); SetNZ(t); Memory.Write(a, t); } break;");
            sb.AppendLine("case 32: P = (byte)(P & 0xFE); break;");
            sb.AppendLine("case 33: P = (byte)(P | 0x01); break;");
            sb.AppendLine("case 34: P = (byte)(P & 0xFB); break;");
            sb.AppendLine("case 35: P = (byte)(P | 0x04); break;");
            sb.AppendLine("case 36: P = (byte)(P & 0xBF); break;");
            sb.AppendLine("case 37: P = (byte)(P & 0xF7); break;");
            sb.AppendLine("case 38: P = (byte)(P | 0x08); break;");
            sb.AppendLine("case 39: Push(A); break;");
            sb.AppendLine("case 40: A = Pull(); SetNZ(A); break;");
            sb.AppendLine("case 41: Push((byte)(P | 0x30)); break;");
            sb.AppendLine("case 42: P = (byte)((Pull() & 0xEF) | 0x20); break;");
            sb.AppendLine("case 43: break;");
            sb.AppendLine("case 50: A = X = val; SetNZ(A); break;");
            sb.AppendLine("case 51: Memory.Write(AddrOp(mode, pc, imm), (byte)(A & X)); break;");
            sb.AppendLine("case 52: { ushort a = AddrOp(mode, pc, imm); byte t = (byte)(Memory.Read(a) - 1); Memory.Write(a, t); Compare(A, t); } break;");
            sb.AppendLine("case 53: { ushort a = AddrOp(mode, pc, imm); byte t = (byte)(Memory.Read(a) + 1); Memory.Write(a, t); Sbc(t); } break;");
            sb.AppendLine("case 54: { ushort a = AddrOp(mode, pc, imm); byte t = Asl(Memory.Read(a)); Memory.Write(a, t); A = (byte)(A | t); SetNZ(A); } break;");
            sb.AppendLine("case 55: { ushort a = AddrOp(mode, pc, imm); byte t = Rol(Memory.Read(a)); Memory.Write(a, t); A = (byte)(A & t); SetNZ(A); } break;");
            sb.AppendLine("case 56: { ushort a = AddrOp(mode, pc, imm); byte t = Lsr(Memory.Read(a)); Memory.Write(a, t); A = (byte)(A ^ t); SetNZ(A); } break;");
            sb.AppendLine("case 57: { ushort a = AddrOp(mode, pc, imm); byte t = Ror(Memory.Read(a)); Memory.Write(a, t); Adc(t); } break;");
            sb.AppendLine("case 58: A = (byte)(A & val); SetCarry((A & 0x80) != 0); SetNZ(A); break;");
            sb.AppendLine("case 59: A = Lsr((byte)(A & val)); break;");
            sb.AppendLine("case 60: A = Ror((byte)(A & val)); break;");
            sb.AppendLine("case 61: A = (byte)((A | 0xEE) & X & val); SetNZ(A); break;");
            sb.AppendLine("case 62: A = (byte)((A | 0xEE) & val); X = A; SetNZ(A); break;");
            sb.AppendLine("case 63: { int t = (A & X) - val; X = (byte)t; SetCarry(t >= 0); SetNZ(X); } break;");
            sb.AppendLine("case 64: Memory.Write(AddrOp(mode, pc, imm), Y); break;");
            sb.AppendLine("case 65: SP = (byte)(SP & val); A = SP; X = SP; SetNZ(SP); break;");
            sb.AppendLine("case 66: Memory.Write(AddrOp(mode, pc, imm), (byte)(A & X)); break;");
            // KIL / unknown -> фатал для интерпретатора (вернём false, Dispatch поставит trap)
            sb.AppendLine("default: DispatchTarget = pc; return false;");
            sb.AppendLine("}");

            sb.AppendLine("pc = (ushort)(pc + len);");
            sb.AppendLine("}");
            // вышли по guard
            sb.AppendLine("DispatchTarget = pc;");
            sb.AppendLine("return false;");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        void AppendByteArrayField(StringBuilder sb, string name, byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                sb.AppendLine("public static readonly byte[] " + name + " = new byte[0];");
                return;
            }

            string b64 = Convert.ToBase64String(data);
            sb.AppendLine("public static readonly byte[] " + name + " = Convert.FromBase64String(");

            int chunk = 65536;
            for (int i = 0; i < b64.Length; i += chunk)
            {
                int len = Math.Min(chunk, b64.Length - i);
                sb.Append("\"" + b64.Substring(i, len) + "\"");
                if (i + chunk < b64.Length) sb.AppendLine(" +");
                else sb.AppendLine(");");
            }
        }

        void AppendMemory(StringBuilder sb)
        {
            sb.AppendLine("static class Memory");
            sb.AppendLine("{");
            sb.AppendLine("public static void Reset()");
            sb.AppendLine("{");
            sb.AppendLine("Array.Clear(Runtime.Ram, 0, Runtime.Ram.Length);");
            sb.AppendLine("Array.Clear(Runtime.SaveRam, 0, Runtime.SaveRam.Length);");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("public static byte Read(ushort addr)");
            sb.AppendLine("{");
            sb.AppendLine("if (addr < 0x2000) return Runtime.Ram[addr & 0x07FF];");
            sb.AppendLine("if (addr < 0x4000) return Ppu.ReadReg((ushort)(0x2000 + (addr & 7)));");
            sb.AppendLine("if (addr < 0x4020) return Apu.ReadReg(addr);");
            sb.AppendLine("if (addr < 0x6000) return MapperStub.Read(addr);");
            sb.AppendLine("if (addr < 0x8000) return Runtime.SaveRam[addr - 0x6000];");
            sb.AppendLine("return ReadPrg(addr);");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("public static void Write(ushort addr, byte value)");
            sb.AppendLine("{");
            sb.AppendLine("if (addr < 0x2000) { Runtime.Ram[addr & 0x07FF] = value; return; }");
            sb.AppendLine("if (addr < 0x4000) { Ppu.WriteReg((ushort)(0x2000 + (addr & 7)), value); return; }");
            sb.AppendLine("if (addr < 0x4020)");
            sb.AppendLine("{");
            sb.AppendLine("    if (addr == 0x4014) { Ppu.DmaOam(value); return; }");
            sb.AppendLine("    Apu.WriteReg(addr, value); return;");
            sb.AppendLine("}");
            sb.AppendLine("if (addr < 0x6000) { MapperStub.Write(addr, value); return; }");
            sb.AppendLine("if (addr < 0x8000) { Runtime.SaveRam[addr - 0x6000] = value; return; }");
            sb.AppendLine("MapperStub.Write(addr, value);");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("public static ushort Read16(ushort addr)");
            sb.AppendLine("{");
            sb.AppendLine("return (ushort)(Read(addr) | (Read((ushort)(addr + 1)) << 8));");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("public static byte ReadPrg(ushort addr)");
            sb.AppendLine("{");
            sb.AppendLine("int len = Runtime.PrgRom.Length;");
            sb.AppendLine("if (len == 0) return 0;");
            sb.AppendLine();
            sb.AppendLine("if (len <= 0x4000)");
            sb.AppendLine("{");
            sb.AppendLine("int a = addr >= 0xC000 ? addr - 0xC000 : addr - 0x8000;");
            sb.AppendLine("if (a < 0) a = addr - 0x8000;");
            sb.AppendLine("return Runtime.PrgRom[a % len];");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("if (len <= 0x8000)");
            sb.AppendLine("{");
            sb.AppendLine("return Runtime.PrgRom[(addr - 0x8000) % len];");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("int baseIndex = len - 0x8000;");
            sb.AppendLine("return Runtime.PrgRom[baseIndex + (addr - 0x8000)];");
            sb.AppendLine("}");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        void AppendPpu(StringBuilder sb)
        {
            sb.AppendLine("static class Ppu");
            sb.AppendLine("{");

            sb.AppendLine("public static volatile byte Ctrl, Mask, Status, OamAddr, ScrollX, ScrollY;");
            sb.AppendLine("public static bool AddrToggle, ScrollToggle;");
            sb.AppendLine("public static volatile ushort VRamAddr;");
            sb.AppendLine("public static byte DataBuffer;");
            sb.AppendLine("public static byte[] VRam = new byte[0x4000];");
            sb.AppendLine("public static byte[] Oam = new byte[256];");
            sb.AppendLine("public static byte[] Palette = new byte[32];");
            sb.AppendLine("public static byte[] Screen = new byte[256 * 240 * 4];");
            sb.AppendLine("public static int FrameCount;");
            sb.AppendLine();

            sb.AppendLine("public static int[] NesPalette = new int[64]");
            sb.AppendLine("{");
            sb.AppendLine("0x7C7C7C, 0x0000FC, 0x0000BC, 0x4428BC, 0x940084, 0xA80020, 0xA81000, 0x881400,");
            sb.AppendLine("0x503000, 0x007800, 0x006800, 0x005800, 0x004058, 0x000000, 0x000000, 0x000000,");
            sb.AppendLine("0xBCBCBC, 0x0078F8, 0x0058F8, 0x6844FC, 0xD800CC, 0xE40058, 0xF83800, 0xE45C10,");
            sb.AppendLine("0xAC7C00, 0x00B800, 0x00A800, 0x00A844, 0x008888, 0x000000, 0x000000, 0x000000,");
            sb.AppendLine("0xF8F8F8, 0x3CBCFC, 0x6888FC, 0x9878F8, 0xF878F8, 0xF85898, 0xF87858, 0xFCA044,");
            sb.AppendLine("0xF8B800, 0xB8F818, 0x58D854, 0x58F898, 0x00E8D8, 0x787878, 0x000000, 0x000000,");
            sb.AppendLine("0xFCFCFC, 0xA4E4FC, 0xB8B8F8, 0xD8B8F8, 0xF8B8F8, 0xF8A4C0, 0xF0D0B0, 0xFCE0A8,");
            sb.AppendLine("0xF8D878, 0xD8F878, 0xB8F8B8, 0xB8F8D8, 0x00FCFC, 0xF8D8F8, 0x000000, 0x000000");
            sb.AppendLine("};");
            sb.AppendLine();

            sb.AppendLine("public static void Reset()");
            sb.AppendLine("{");
            sb.AppendLine("    Ctrl = Mask = OamAddr = ScrollX = ScrollY = 0;");
            sb.AppendLine("    Status = 0x80;");
            sb.AppendLine("    AddrToggle = false;");
            sb.AppendLine("    ScrollToggle = false;");
            sb.AppendLine("    VRamAddr = 0;");
            sb.AppendLine("    DataBuffer = 0;");
            sb.AppendLine("    FrameCount = 0;");
            sb.AppendLine();
            sb.AppendLine("    Array.Clear(VRam, 0, VRam.Length);");
            sb.AppendLine("    Array.Clear(Oam, 0, Oam.Length);");
            sb.AppendLine("    Array.Clear(Palette, 0, Palette.Length);");
            sb.AppendLine("    Array.Clear(Screen, 0, Screen.Length);");
            sb.AppendLine("    for (int i = 0; i < Palette.Length; i++) Palette[i] = (byte)(0x01 + (i * 3) % 0x3D);");
            sb.AppendLine("    Palette[0] = 0x0F;");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("public static byte ReadReg(ushort reg)");
            sb.AppendLine("{");
            sb.AppendLine("    switch (reg & 7)");
            sb.AppendLine("    {");
            sb.AppendLine("        case 2:");
            sb.AppendLine("        {");
            sb.AppendLine("            byte s = Status;");
            sb.AppendLine("            Status = (byte)(Status & 0x7F);");
            sb.AppendLine("            AddrToggle = false;");
            sb.AppendLine("            ScrollToggle = false;");
            sb.AppendLine("            Thread.Sleep(0);");
            sb.AppendLine("            return s;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        case 4:");
            sb.AppendLine("            return Oam[OamAddr];");
            sb.AppendLine();
            sb.AppendLine("        case 7:");
            sb.AppendLine("        {");
            sb.AppendLine("            ushort addr = (ushort)(VRamAddr & 0x3FFF);");
            sb.AppendLine("            byte data = ReadVram(addr);");
            sb.AppendLine("            byte ret;");
            sb.AppendLine();
            sb.AppendLine("            if (addr >= 0x3F00)");
            sb.AppendLine("            {");
            sb.AppendLine("                ret = data;");
            sb.AppendLine("                DataBuffer = ReadVram((ushort)(addr - 0x1000));");
            sb.AppendLine("            }");
            sb.AppendLine("            else");
            sb.AppendLine("            {");
            sb.AppendLine("                ret = DataBuffer;");
            sb.AppendLine("                DataBuffer = data;");
            sb.AppendLine("            }");
            sb.AppendLine();
            sb.AppendLine("            VRamAddr = (ushort)(VRamAddr + ((Ctrl & 0x04) != 0 ? 32 : 1));");
            sb.AppendLine("            return ret;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        default:");
            sb.AppendLine("            return 0;");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("public static void WriteReg(ushort reg, byte value)");
            sb.AppendLine("{");
            sb.AppendLine("    switch (reg & 7)");
            sb.AppendLine("    {");
            sb.AppendLine("        case 0: Ctrl = value; break;");
            sb.AppendLine("        case 1: Mask = value; break;");
            sb.AppendLine("        case 3: OamAddr = value; break;");
            sb.AppendLine();
            sb.AppendLine("        case 4:");
            sb.AppendLine("            Oam[OamAddr] = value;");
            sb.AppendLine("            OamAddr = (byte)(OamAddr + 1);");
            sb.AppendLine("            break;");
            sb.AppendLine();
            sb.AppendLine("        case 5:");
            sb.AppendLine("            if (!ScrollToggle) ScrollX = value;");
            sb.AppendLine("            else ScrollY = value;");
            sb.AppendLine("            ScrollToggle = !ScrollToggle;");
            sb.AppendLine("            break;");
            sb.AppendLine();
            sb.AppendLine("        case 6:");
            sb.AppendLine("            if (!AddrToggle) VRamAddr = (ushort)((value & 0x3F) << 8);");
            sb.AppendLine("            else VRamAddr = (ushort)((VRamAddr & 0xFF00) | value);");
            sb.AppendLine("            AddrToggle = !AddrToggle;");
            sb.AppendLine("            break;");
            sb.AppendLine();
            sb.AppendLine("        case 7:");
            sb.AppendLine("            WriteVram(VRamAddr, value);");
            sb.AppendLine("            VRamAddr = (ushort)(VRamAddr + ((Ctrl & 0x04) != 0 ? 32 : 1));");
            sb.AppendLine("            break;");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("public static void DmaOam(byte page)");
            sb.AppendLine("{");
            sb.AppendLine("    int baseAddr = page << 8;");
            sb.AppendLine("    for (int i = 0; i < 256; i++)");
            sb.AppendLine("    {");
            sb.AppendLine("        Oam[i] = Memory.Read((ushort)(baseAddr + i));");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("static ushort MirrorName(ushort addr)");
            sb.AppendLine("{");
            sb.AppendLine("    int table = (addr >> 10) & 3;");
            sb.AppendLine("    int offset = addr & 0x3FF;");
            sb.AppendLine();
            sb.AppendLine("    if (Runtime.Mirroring == 1)");
            sb.AppendLine("    {");
            sb.AppendLine("        // Vertical mirroring:");
            sb.AppendLine("        // 0 mirrors 2, 1 mirrors 3");
            sb.AppendLine("        if (table == 2) table = 0;");
            sb.AppendLine("        else if (table == 3) table = 1;");
            sb.AppendLine("    }");
            sb.AppendLine("    else if (Runtime.Mirroring == 0)");
            sb.AppendLine("    {");
            sb.AppendLine("        // Horizontal mirroring:");
            sb.AppendLine("        // 0 mirrors 1, 2 mirrors 3");
            sb.AppendLine("        if (table == 1) table = 0;");
            sb.AppendLine("        else if (table >= 2) table = 1;");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    return (ushort)(0x2000 + table * 0x400 + offset);");
            sb.AppendLine("}");

            sb.AppendLine("static ushort NormalizeVram(ushort addr)");
            sb.AppendLine("{");
            sb.AppendLine("    addr = (ushort)(addr & 0x3FFF);");
            sb.AppendLine();
            sb.AppendLine("    if (addr >= 0x3000 && addr < 0x3F00)");
            sb.AppendLine("        addr = (ushort)(addr - 0x1000);");
            sb.AppendLine();
            sb.AppendLine("    if (addr >= 0x2000 && addr < 0x3000)");
            sb.AppendLine("        return MirrorName(addr);");
            sb.AppendLine();
            sb.AppendLine("    if (addr >= 0x3F00)");
            sb.AppendLine("    {");
            sb.AppendLine("        int idx = (addr - 0x3F00) & 0x1F;");
            sb.AppendLine();
            sb.AppendLine("        // Palette mirrors:");
            sb.AppendLine("        // 0x3F10/14/18/1C -> 0x3F00/04/08/0C");
            sb.AppendLine("        if ((idx & 0x13) == 0x10)");
            sb.AppendLine("            idx &= 0x0F;");
            sb.AppendLine();
            sb.AppendLine("        return (ushort)(0x3F00 + idx);");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    return addr;");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("static byte ReadVram(ushort addr)");
            sb.AppendLine("{");
            sb.AppendLine("    addr = NormalizeVram(addr);");
            sb.AppendLine();
            sb.AppendLine("    if (addr >= 0x3F00)");
            sb.AppendLine("        return Palette[(addr - 0x3F00) & 0x1F];");
            sb.AppendLine();
            sb.AppendLine("    return VRam[addr];");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("static void WriteVram(ushort addr, byte value)");
            sb.AppendLine("{");
            sb.AppendLine("    addr = NormalizeVram(addr);");
            sb.AppendLine();
            sb.AppendLine("    if (addr >= 0x3F00)");
            sb.AppendLine("        Palette[(addr - 0x3F00) & 0x1F] = value;");
            sb.AppendLine("    else");
            sb.AppendLine("        VRam[addr] = value;");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("public static void RenderToBitmap(Bitmap target)");
            sb.AppendLine("{");
            sb.AppendLine("    lock (Runtime.FrameLock)");
            sb.AppendLine("    {");
            sb.AppendLine("        RenderScreen();");
            sb.AppendLine();
            sb.AppendLine("        Rectangle rc = new Rectangle(0, 0, 256, 240);");
            sb.AppendLine("        BitmapData data = target.LockBits(rc, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);");
            sb.AppendLine();
            sb.AppendLine("        if (data.Stride == 256 * 4)");
            sb.AppendLine("        {");
            sb.AppendLine("            Marshal.Copy(Screen, 0, data.Scan0, Screen.Length);");
            sb.AppendLine("        }");
            sb.AppendLine("        else");
            sb.AppendLine("        {");
            sb.AppendLine("            for (int y = 0; y < 240; y++)");
            sb.AppendLine("            {");
            sb.AppendLine("                IntPtr dst = new IntPtr(data.Scan0.ToInt64() + y * data.Stride);");
            sb.AppendLine("                Marshal.Copy(Screen, y * 256 * 4, dst, 256 * 4);");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        target.UnlockBits(data);");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("public static void RenderScreen()");
            sb.AppendLine("{");
            sb.AppendLine("    int backdrop = Palette[0] & 0x3F;");
            sb.AppendLine("    FillScreen(backdrop);");
            sb.AppendLine();
            sb.AppendLine("    bool showBG = (Mask & 0x08) != 0;");
            sb.AppendLine("    bool showSpr = (Mask & 0x10) != 0;");
            sb.AppendLine();
            sb.AppendLine("    // Если маска еще не настроена, показываем debug-рендер.");
            sb.AppendLine("    // DEBUG: force render even if rendering disabled");
            sb.AppendLine("    RenderBackground();");
            sb.AppendLine("    RenderSprites();");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("static void FillScreen(int colorIndex)");
            sb.AppendLine("{");
            sb.AppendLine("    int rgb = NesPalette[colorIndex & 0x3F];");
            sb.AppendLine("    byte b = (byte)(rgb & 0xFF);");
            sb.AppendLine("    byte g = (byte)((rgb >> 8) & 0xFF);");
            sb.AppendLine("    byte r = (byte)((rgb >> 16) & 0xFF);");
            sb.AppendLine();
            sb.AppendLine("    for (int i = 0; i < Screen.Length; i += 4)");
            sb.AppendLine("    {");
            sb.AppendLine("        Screen[i + 0] = b;");
            sb.AppendLine("        Screen[i + 1] = g;");
            sb.AppendLine("        Screen[i + 2] = r;");
            sb.AppendLine("        Screen[i + 3] = 0xFF;");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("static void SetPixel(int x, int y, int colorIndex)");
            sb.AppendLine("{");
            sb.AppendLine("    if ((uint)x >= 256 || (uint)y >= 240) return;");
            sb.AppendLine();
            sb.AppendLine("    int rgb = NesPalette[colorIndex & 0x3F];");
            sb.AppendLine("    int off = (y * 256 + x) * 4;");
            sb.AppendLine();
            sb.AppendLine("    Screen[off + 0] = (byte)(rgb & 0xFF);");
            sb.AppendLine("    Screen[off + 1] = (byte)((rgb >> 8) & 0xFF);");
            sb.AppendLine("    Screen[off + 2] = (byte)((rgb >> 16) & 0xFF);");
            sb.AppendLine("    Screen[off + 3] = 0xFF;");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("static void RenderBackground()");
            sb.AppendLine("{");
            sb.AppendLine("    if (Runtime.ChrRom.Length == 0) return;");
            sb.AppendLine();
            sb.AppendLine("    int nameBase;");
            sb.AppendLine("    int scrollX = ScrollX;");
            sb.AppendLine("    int scrollY = ScrollY;");
            sb.AppendLine();
            sb.AppendLine("    // Если PPUADDR похож на scroll/nametable address, пробуем использовать его.");
            sb.AppendLine("    if (VRamAddr >= 0x2000 && VRamAddr < 0x3000 && (VRamAddr & 0x3FF) < 0x3C0)");
            sb.AppendLine("    {");
            sb.AppendLine("        nameBase = MirrorName((ushort)(VRamAddr & 0xFC00));");
            sb.AppendLine();
            sb.AppendLine("        if (ScrollX == 0 && ScrollY == 0)");
            sb.AppendLine("        {");
            sb.AppendLine("            scrollX = (VRamAddr & 0x1F) * 8;");
            sb.AppendLine("            scrollY = ((VRamAddr >> 5) & 0x1F) * 8;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("    else");
            sb.AppendLine("    {");
            sb.AppendLine("        nameBase = MirrorName((ushort)(0x2000 + (Ctrl & 0x03) * 0x400));");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    int patternBase = (Ctrl & 0x10) != 0 ? 0x1000 : 0x0000;");
            sb.AppendLine();
            sb.AppendLine("    for (int y = 0; y < 240; y++)");
            sb.AppendLine("    {");
            sb.AppendLine("        int srcY = (y + scrollY) % 240;");
            sb.AppendLine("        int tileY = srcY >> 3;");
            sb.AppendLine("        int py = srcY & 7;");
            sb.AppendLine();
            sb.AppendLine("        for (int x = 0; x < 256; x++)");
            sb.AppendLine("        {");
            sb.AppendLine("            int srcX = (x + scrollX) & 0xFF;");
            sb.AppendLine("            int tileX = srcX >> 3;");
            sb.AppendLine("            int px = srcX & 7;");
            sb.AppendLine();
            sb.AppendLine("            int tileAddr = nameBase + tileY * 32 + tileX;");
            sb.AppendLine("            byte tile = VRam[tileAddr];");
            sb.AppendLine();
            sb.AppendLine("            int pal = GetAttributePalette(nameBase, tileX, tileY);");
            sb.AppendLine("            int color = GetPatternPixel(patternBase, tile, px, py);");
            sb.AppendLine();
            sb.AppendLine("            if (color == 0) continue;");
            sb.AppendLine();
            sb.AppendLine("            int finalColor = GetBackgroundColor(pal, color);");
            sb.AppendLine("            SetPixel(x, y, finalColor);");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("static int GetAttributePalette(int nameBase, int tileX, int tileY)");
            sb.AppendLine("{");
            sb.AppendLine("    int attrBase = nameBase + 0x3C0;");
            sb.AppendLine("    int ax = tileX >> 2;");
            sb.AppendLine("    int ay = tileY >> 2;");
            sb.AppendLine();
            sb.AppendLine("    int attr = VRam[attrBase + ay * 8 + ax];");
            sb.AppendLine("    int shift = ((tileY & 2) << 1) | (tileX & 2);");
            sb.AppendLine();
            sb.AppendLine("    return (attr >> shift) & 3;");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("static int GetBackgroundColor(int pal, int color)");
            sb.AppendLine("{");
            sb.AppendLine("    if (color == 0)");
            sb.AppendLine("        return Palette[0] & 0x3F;");
            sb.AppendLine();
            sb.AppendLine("    int idx = 1 + pal * 4 + (color - 1);");
            sb.AppendLine("    return Palette[idx & 0x1F] & 0x3F;");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("static int GetPatternPixel(int patternBase, int tile, int px, int py)");
            sb.AppendLine("{");
            sb.AppendLine("    int len = Runtime.ChrRom.Length;");
            sb.AppendLine("    if (len == 0) return 0;");
            sb.AppendLine();
            sb.AppendLine("    int addr = (patternBase + tile * 16 + py) % len;");
            sb.AppendLine("    int addr2 = (addr + 8) % len;");
            sb.AppendLine();
            sb.AppendLine("    byte low = Runtime.ChrRom[addr];");
            sb.AppendLine("    byte high = Runtime.ChrRom[addr2];");
            sb.AppendLine();
            sb.AppendLine("    int bit = 7 - px;");
            sb.AppendLine("    return ((low >> bit) & 1) | (((high >> bit) & 1) << 1);");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("static void RenderSprites()");
            sb.AppendLine("{");
            sb.AppendLine("    if (Runtime.ChrRom.Length == 0) return;");
            sb.AppendLine();
            sb.AppendLine("    bool big = (Ctrl & 0x20) != 0;");
            sb.AppendLine("    int defaultPatternBase = (Ctrl & 0x08) != 0 ? 0x1000 : 0x0000;");
            sb.AppendLine();
            sb.AppendLine("    // Рисуем в обратном порядке, чтобы спрайт 0 имел наивысший приоритет.");
            sb.AppendLine("    for (int i = 63; i >= 0; i--)");
            sb.AppendLine("    {");
            sb.AppendLine("        int y = Oam[i * 4] + 1;");
            sb.AppendLine("        int tile = Oam[i * 4 + 1];");
            sb.AppendLine("        int attr = Oam[i * 4 + 2];");
            sb.AppendLine("        int x = Oam[i * 4 + 3];");
            sb.AppendLine();
            sb.AppendLine("        if (y >= 240 || y <= -16) continue;");
            sb.AppendLine();
            sb.AppendLine("        int pal = attr & 3;");
            sb.AppendLine("        bool flipH = (attr & 0x40) != 0;");
            sb.AppendLine("        bool flipV = (attr & 0x80) != 0;");
            sb.AppendLine();
            sb.AppendLine("        int height = big ? 16 : 8;");
            sb.AppendLine("        int patternBase;");
            sb.AppendLine("        int tileIndex;");
            sb.AppendLine();
            sb.AppendLine("        if (big)");
            sb.AppendLine("        {");
            sb.AppendLine("            patternBase = (tile & 1) != 0 ? 0x1000 : 0x0000;");
            sb.AppendLine("            tileIndex = tile & 0xFE;");
            sb.AppendLine("        }");
            sb.AppendLine("        else");
            sb.AppendLine("        {");
            sb.AppendLine("            patternBase = defaultPatternBase;");
            sb.AppendLine("            tileIndex = tile;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        for (int py = 0; py < height; py++)");
            sb.AppendLine("        {");
            sb.AppendLine("            int screenY = y + py;");
            sb.AppendLine("            if ((uint)screenY >= 240) continue;");
            sb.AppendLine();
            sb.AppendLine("            int srcY = flipV ? (height - 1 - py) : py;");
            sb.AppendLine("            int t = tileIndex;");
            sb.AppendLine("            int row = srcY;");
            sb.AppendLine();
            sb.AppendLine("            if (big && srcY >= 8)");
            sb.AppendLine("            {");
            sb.AppendLine("                t = tileIndex + 1;");
            sb.AppendLine("                row = srcY - 8;");
            sb.AppendLine("            }");
            sb.AppendLine();
            sb.AppendLine("            for (int px = 0; px < 8; px++)");
            sb.AppendLine("            {");
            sb.AppendLine("                int screenX = x + px;");
            sb.AppendLine("                if ((uint)screenX >= 256) continue;");
            sb.AppendLine();
            sb.AppendLine("                int srcX = flipH ? (7 - px) : px;");
            sb.AppendLine("                int color = GetPatternPixel(patternBase, t, srcX, row);");
            sb.AppendLine();
            sb.AppendLine("                if (color == 0) continue;");
            sb.AppendLine();
            sb.AppendLine("                if (i == 0)");
            sb.AppendLine("                    Status = (byte)(Status | 0x40);");
            sb.AppendLine();
            sb.AppendLine("                int idx = 0x11 + pal * 4 + (color - 1);");
            sb.AppendLine("                SetPixel(screenX, screenY, Palette[idx & 0x1F] & 0x3F);");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("public static void Render(Graphics g)");
            sb.AppendLine("{");
            sb.AppendLine("    lock (Runtime.FrameLock)");
            sb.AppendLine("    {");
            sb.AppendLine("        g.Clear(Color.Black);");
            sb.AppendLine("        g.DrawString(\"PPU STUB\", SystemFonts.DefaultFont, Brushes.Lime, 4, 4);");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            sb.AppendLine("}");
            sb.AppendLine();
        }

        void AppendApu(StringBuilder sb)
        {
            sb.AppendLine("static class Apu");
            sb.AppendLine("{");
            sb.AppendLine("public static byte[] Regs = new byte[0x18];");
            sb.AppendLine("public static volatile int Period1, Period2, PeriodT;");
            sb.AppendLine("public static volatile int Len1, Len2, LenT, LenN;");
            sb.AppendLine("public static volatile int LinT;");
            sb.AppendLine("public static volatile bool LinReload;");
            sb.AppendLine("static int _lenDiv;");
            sb.AppendLine("public static readonly int[] LengthTable = new int[32] {10,254,20,2,40,4,80,6,160,8,60,10,14,12,26,14,12,16,24,18,48,20,96,22,192,24,72,26,16,28,32,30};");
            sb.AppendLine("public static void Reset()");
            sb.AppendLine("{");
            sb.AppendLine("    Array.Clear(Regs, 0, Regs.Length);");
            sb.AppendLine("    Period1 = Period2 = PeriodT = 0; Len1 = Len2 = LenT = LenN = 0; LinT = 0; LinReload = false; _lenDiv = 0;");
            sb.AppendLine("    Audio.TryStart();");
            sb.AppendLine("}");
            sb.AppendLine("public static byte ReadReg(ushort addr)");
            sb.AppendLine("{");
            sb.AppendLine("    if (addr == 0x4016) return Runtime.JoyRead1();");
            sb.AppendLine("    if (addr == 0x4017) return Runtime.JoyRead2();");
            sb.AppendLine("    if (addr == 0x4015) return 0;");
            sb.AppendLine("    if (addr >= 0x4000 && addr <= 0x4017) return Regs[addr - 0x4000];");
            sb.AppendLine("    return 0;");
            sb.AppendLine("}");
            sb.AppendLine("public static void WriteReg(ushort addr, byte value)");
            sb.AppendLine("{");
            sb.AppendLine("    if (addr == 0x4016) { Runtime.JoyStrobe(value); Regs[addr - 0x4000] = value; return; }");
            sb.AppendLine("    if (addr >= 0x4000 && addr <= 0x4017) Regs[addr - 0x4000] = value;");
            sb.AppendLine("    switch (addr)");
            sb.AppendLine("    {");
            sb.AppendLine("        case 0x4003: Period1 = ((value & 7) << 8) | Regs[0x02]; if ((Regs[0x00] & 0x20) == 0) Len1 = LengthTable[(value >> 3) & 0x1F]; break;");
            sb.AppendLine("        case 0x4007: Period2 = ((value & 7) << 8) | Regs[0x06]; if ((Regs[0x04] & 0x20) == 0) Len2 = LengthTable[(value >> 3) & 0x1F]; break;");
            sb.AppendLine("        case 0x400B: PeriodT = ((value & 7) << 8) | Regs[0x0A]; if ((Regs[0x08] & 0x80) == 0) LenT = LengthTable[(value >> 3) & 0x1F]; LinReload = true; break;");
            sb.AppendLine("        case 0x400F: if ((Regs[0x0C] & 0x20) == 0) LenN = LengthTable[(value >> 3) & 0x1F]; break;");
            sb.AppendLine("        case 0x4015:");
            sb.AppendLine("            if ((value & 1) == 0) Len1 = 0;");
            sb.AppendLine("            if ((value & 2) == 0) Len2 = 0;");
            sb.AppendLine("            if ((value & 4) == 0) LenT = 0;");
            sb.AppendLine("            if ((value & 8) == 0) LenN = 0;");
            sb.AppendLine("            Audio.OnStatusChange(value);");
            sb.AppendLine("            break;");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine("public static void Clock240()");
            sb.AppendLine("{");
            sb.AppendLine("    if (LinReload) LinT = Regs[0x08] & 0x7F;");
            sb.AppendLine("    else if (LinT > 0) LinT--;");
            sb.AppendLine("    if ((Regs[0x08] & 0x80) == 0) LinReload = false;");
            sb.AppendLine("    _lenDiv++;");
            sb.AppendLine("    if (_lenDiv >= 2)");
            sb.AppendLine("    {");
            sb.AppendLine("        _lenDiv = 0;");
            sb.AppendLine("        if (Len1 > 0 && (Regs[0x00] & 0x20) == 0) Len1--;");
            sb.AppendLine("        if (Len2 > 0 && (Regs[0x04] & 0x20) == 0) Len2--;");
            sb.AppendLine("        if (LenT > 0 && (Regs[0x08] & 0x80) == 0) LenT--;");
            sb.AppendLine("        if (LenN > 0 && (Regs[0x0C] & 0x20) == 0) LenN--;");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine("public static bool Sq1On() { return (Regs[0x15] & 1) != 0 && Len1 > 0; }");
            sb.AppendLine("public static bool Sq2On() { return (Regs[0x15] & 2) != 0 && Len2 > 0; }");
            sb.AppendLine("public static bool TriOn() { return (Regs[0x15] & 4) != 0 && LenT > 0 && LinT > 0; }");
            sb.AppendLine("public static bool NoiOn() { return (Regs[0x15] & 8) != 0 && LenN > 0; }");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        void AppendMapper(StringBuilder sb)
        {
            sb.AppendLine("static class MapperStub");
            sb.AppendLine("{");
            sb.AppendLine("public static void Reset() { }");
            sb.AppendLine("public static byte Read(ushort addr) { return 0x40; }");
            sb.AppendLine("public static void Write(ushort addr, byte value) { }");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        void AppendAudio(StringBuilder sb)
        {
            sb.AppendLine("static class Audio");
            sb.AppendLine("{");
            sb.AppendLine("    [StructLayout(LayoutKind.Sequential)]");
            sb.AppendLine("    struct WAVEFORMATEX { public ushort wFormatTag; public ushort nChannels; public uint nSamplesPerSec; public uint nAvgBytesPerSec; public ushort nBlockAlign; public ushort wBitsPerSample; public ushort cbSize; }");
            sb.AppendLine("    [StructLayout(LayoutKind.Sequential)]");
            sb.AppendLine("    struct WAVEHDR { public IntPtr lpData; public uint dwBufferLength; public uint dwBytesRecorded; public IntPtr dwUser; public uint dwFlags; public uint dwLoops; public IntPtr lpNext; public IntPtr reserved; }");
            sb.AppendLine("    [DllImport(\"winmm.dll\")] static extern int waveOutOpen(out IntPtr h, int dev, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, int flags);");
            sb.AppendLine("    [DllImport(\"winmm.dll\")] static extern int waveOutPrepareHeader(IntPtr h, ref WAVEHDR hdr, int size);");
            sb.AppendLine("    [DllImport(\"winmm.dll\")] static extern int waveOutWrite(IntPtr h, ref WAVEHDR hdr, int size);");
            sb.AppendLine("    [DllImport(\"winmm.dll\")] static extern int waveOutUnprepareHeader(IntPtr h, ref WAVEHDR hdr, int size);");
            sb.AppendLine("    [DllImport(\"winmm.dll\")] static extern int waveOutReset(IntPtr h);");
            sb.AppendLine("    [DllImport(\"winmm.dll\")] static extern int waveOutClose(IntPtr h);");
            sb.AppendLine();
            sb.AppendLine("    const int SR = 22050;");
            sb.AppendLine("    const int BLOCK = 1024;");
            sb.AppendLine("    static IntPtr _hwo;");
            sb.AppendLine("    static bool _open;");
            sb.AppendLine("    static Thread _thread;");
            sb.AppendLine("    static volatile bool _run;");
            sb.AppendLine("    static byte[][] _buf = new byte[2][];");
            sb.AppendLine("    static WAVEHDR[] _hdr = new WAVEHDR[2];");
            sb.AppendLine("    static GCHandle[] _pin = new GCHandle[2];");
            sb.AppendLine("    static bool[] _queued = new bool[2];");
            sb.AppendLine("    static float _ph1, _ph2, _phT;");
            sb.AppendLine("    static int _noise = 1;");
            sb.AppendLine("    static int _noiseCounter;");
            sb.AppendLine("    static int _qAcc;");
            sb.AppendLine("    static readonly int[] NoisePeriod = new int[16] {4,8,16,32,64,96,128,160,202,254,380,508,762,1016,2034,4068};");
            sb.AppendLine("    static readonly float[] DutyF = new float[4] {0.125f,0.25f,0.5f,0.75f};");
            sb.AppendLine();
            sb.AppendLine("    public static void OnStatusChange(byte v) { }");
            sb.AppendLine();
            sb.AppendLine("    public static void TryStart()");
            sb.AppendLine("    {");
            sb.AppendLine("        try");
            sb.AppendLine("        {");
            sb.AppendLine("            if (_open) return;");
            sb.AppendLine("            WAVEFORMATEX fmt = new WAVEFORMATEX();");
            sb.AppendLine("            fmt.wFormatTag = 1; fmt.nChannels = 1; fmt.nSamplesPerSec = SR; fmt.wBitsPerSample = 8; fmt.nBlockAlign = 1; fmt.nAvgBytesPerSec = SR; fmt.cbSize = 0;");
            sb.AppendLine("            int r = waveOutOpen(out _hwo, -1, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);");
            sb.AppendLine("            if (r != 0) return;");
            sb.AppendLine("            _open = true;");
            sb.AppendLine("            int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));");
            sb.AppendLine("            for (int i = 0; i < 2; i++)");
            sb.AppendLine("            {");
            sb.AppendLine("                _buf[i] = new byte[BLOCK];");
            sb.AppendLine("                _pin[i] = GCHandle.Alloc(_buf[i], GCHandleType.Pinned);");
            sb.AppendLine("                _hdr[i] = new WAVEHDR();");
            sb.AppendLine("                _hdr[i].lpData = _pin[i].AddrOfPinnedObject();");
            sb.AppendLine("                _hdr[i].dwBufferLength = BLOCK;");
            sb.AppendLine("                waveOutPrepareHeader(_hwo, ref _hdr[i], hdrSize);");
            sb.AppendLine("            }");
            sb.AppendLine("            _run = true;");
            sb.AppendLine("            _thread = new Thread(new ThreadStart(ThreadProc));");
            sb.AppendLine("            _thread.IsBackground = true;");
            sb.AppendLine("            _thread.Priority = ThreadPriority.Lowest;");
            sb.AppendLine("            _thread.Start();");
            sb.AppendLine("        }");
            sb.AppendLine("        catch { _open = false; }");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    public static void TryStop()");
            sb.AppendLine("    {");
            sb.AppendLine("        try");
            sb.AppendLine("        {");
            sb.AppendLine("            _run = false;");
            sb.AppendLine("            if (_open)");
            sb.AppendLine("            {");
            sb.AppendLine("                waveOutReset(_hwo);");
            sb.AppendLine("                int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));");
            sb.AppendLine("                for (int i = 0; i < 2; i++) { try { waveOutUnprepareHeader(_hwo, ref _hdr[i], hdrSize); } catch { } if (_pin[i].IsAllocated) _pin[i].Free(); }");
            sb.AppendLine("                waveOutClose(_hwo);");
            sb.AppendLine("                _open = false;");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine("        catch { }");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    static void ThreadProc()");
            sb.AppendLine("    {");
            sb.AppendLine("        int hdrSize; try { hdrSize = Marshal.SizeOf(typeof(WAVEHDR)); } catch { hdrSize = 32; }");
            sb.AppendLine("        int idx = 0;");
            sb.AppendLine("        while (_run)");
            sb.AppendLine("        {");
            sb.AppendLine("            try");
            sb.AppendLine("            {");
            sb.AppendLine("                if (_queued[idx]) { int w = 0; while (_run && (_hdr[idx].dwFlags & 1) == 0) { Thread.Sleep(1); if (++w > 300) break; } }");
            sb.AppendLine("                Fill(_buf[idx]);");
            sb.AppendLine("                waveOutWrite(_hwo, ref _hdr[idx], hdrSize);");
            sb.AppendLine("                _queued[idx] = true;");
            sb.AppendLine("                idx = 1 - idx;");
            sb.AppendLine("            }");
            sb.AppendLine("            catch { break; }");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    static void Fill(byte[] b)");
            sb.AppendLine("    {");
            sb.AppendLine("        _qAcc += b.Length; while (_qAcc >= 91) { _qAcc -= 91; Apu.Clock240(); }");
            sb.AppendLine("        int per1 = Apu.Period1 + 1;");
            sb.AppendLine("        int per2 = Apu.Period2 + 1;");
            sb.AppendLine("        int perT = Apu.PeriodT + 1;");
            sb.AppendLine("        int nperIdx = Apu.Regs[0xE] & 0xF; int nper = NoisePeriod[nperIdx];");
            sb.AppendLine("        int nmode = (Apu.Regs[0xE] & 0x80) != 0 ? 6 : 1;");
            sb.AppendLine("        float f1 = per1 > 8 ? (1789773f / (16f * per1)) : 0f;");
            sb.AppendLine("        float f2 = per2 > 8 ? (1789773f / (16f * per2)) : 0f;");
            sb.AppendLine("        float fT = perT > 1 ? (1789773f / (32f * perT)) : 0f;");
            sb.AppendLine("        int v1 = Apu.Sq1On() ? (Apu.Regs[0] & 0xF) : 0;");
            sb.AppendLine("        int v2 = Apu.Sq2On() ? (Apu.Regs[4] & 0xF) : 0;");
            sb.AppendLine("        int vT = Apu.TriOn() ? 12 : 0;");
            sb.AppendLine("        int vN = Apu.NoiOn() ? (Apu.Regs[0xC] & 0xF) : 0;");
            sb.AppendLine("        float d1f = DutyF[(Apu.Regs[0] >> 6) & 3];");
            sb.AppendLine("        float d2f = DutyF[(Apu.Regs[4] >> 6) & 3];");
            sb.AppendLine("        for (int i = 0; i < b.Length; i++)");
            sb.AppendLine("        {");
            sb.AppendLine("            int s = 0;");
            sb.AppendLine("            if (v1 != 0 && f1 > 0) { _ph1 += f1 / SR; if (_ph1 >= 1f) _ph1 -= 1f; if (_ph1 < d1f) s += v1; }");
            sb.AppendLine("            if (v2 != 0 && f2 > 0) { _ph2 += f2 / SR; if (_ph2 >= 1f) _ph2 -= 1f; if (_ph2 < d2f) s += v2; }");
            sb.AppendLine("            if (vT != 0 && fT > 0) { _phT += fT / SR; if (_phT >= 1f) _phT -= 1f; s += (_phT < 0.5f ? vT : -vT); }");
            sb.AppendLine("            if (vN != 0) { _noiseCounter++; if (_noiseCounter >= nper) { _noiseCounter = 0; int bit = (_noise & 1) ^ ((_noise >> nmode) & 1); _noise = (_noise >> 1) | (bit << 14); } if ((_noise & 1) == 0) s += vN; }");
            sb.AppendLine("            int sample = 128 + s * 2;");
            sb.AppendLine("            if (sample < 0) sample = 0; if (sample > 255) sample = 255;");
            sb.AppendLine("            b[i] = (byte)sample;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        void AppendForm(StringBuilder sb)
        {
            sb.AppendLine("class PpuForm : Form");
            sb.AppendLine("{");
            sb.AppendLine("private System.Windows.Forms.Timer _timer;");
            sb.AppendLine("private Bitmap _back;");
            sb.AppendLine();

            sb.AppendLine("public PpuForm()");
            sb.AppendLine("{");
            sb.AppendLine("    Text = \"NES Lifted: \" + Runtime.GameName;");
            sb.AppendLine("    ClientSize = new Size(256, 240);");
            sb.AppendLine("    FormBorderStyle = FormBorderStyle.FixedSingle;");
            sb.AppendLine("    MaximizeBox = false;");
            sb.AppendLine("    StartPosition = FormStartPosition.CenterScreen;");
            sb.AppendLine();
            sb.AppendLine("    SetStyle(");
            sb.AppendLine("        ControlStyles.AllPaintingInWmPaint |");
            sb.AppendLine("        ControlStyles.UserPaint |");
            sb.AppendLine("        ControlStyles.OptimizedDoubleBuffer,");
            sb.AppendLine("        true);");
            sb.AppendLine();
            sb.AppendLine("    _back = new Bitmap(256, 240, PixelFormat.Format32bppArgb);");
            sb.AppendLine();
            sb.AppendLine("    _timer = new System.Windows.Forms.Timer();");
            sb.AppendLine("    _timer.Interval = 16;");
            sb.AppendLine();
            sb.AppendLine("    _timer.Tick += delegate(object sender, EventArgs e)");
            sb.AppendLine("    {");
            sb.AppendLine("        Ppu.FrameCount++;");
            sb.AppendLine();
            sb.AppendLine("        // VBlank + clear sprite0 hit at frame start.");
            sb.AppendLine("        Ppu.Status = (byte)((Ppu.Status & 0xBF) | 0x80);");
            sb.AppendLine();
            sb.AppendLine("        // Если игра включила NMI в PPUCTRL, симулируем NMI в следующем safe-point.");
            sb.AppendLine("        if ((Ppu.Ctrl & 0x80) != 0 && !Runtime.InNmi) Runtime.NmiPending = true;");
            sb.AppendLine();
            sb.AppendLine("        Ppu.RenderToBitmap(_back);");
            sb.AppendLine("        Invalidate();");
            sb.AppendLine("    };");
            sb.AppendLine();
            sb.AppendLine("    _timer.Start();");
            sb.AppendLine();
            sb.AppendLine("    this.KeyPreview = true;");
            sb.AppendLine("    this.KeyDown += delegate(object s, KeyEventArgs ke) { SetJoyKey(ke.KeyCode, true); ke.SuppressKeyPress = true; };");
            sb.AppendLine("    this.KeyUp += delegate(object s, KeyEventArgs ke) { SetJoyKey(ke.KeyCode, false); };");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("protected override void OnPaint(PaintEventArgs e)");
            sb.AppendLine("{");
            sb.AppendLine("    base.OnPaint(e);");
            sb.AppendLine();
            sb.AppendLine("    if (_back != null)");
            sb.AppendLine("        e.Graphics.DrawImageUnscaled(_back, 0, 0);");
            sb.AppendLine();
            sb.AppendLine("    string line1 = string.Format(\"F:{0} I:{1} PC:{2:X4} T:{3}\", Ppu.FrameCount, Runtime.InsCount, Runtime.LastPC, Runtime.TrapCount);");
            sb.AppendLine("    string line2 = string.Format(\"CTRL:{0:X2} MASK:{1:X2} ST:{2:X2} VRAM:{3:X4}\", Ppu.Ctrl, Ppu.Mask, Ppu.Status, Ppu.VRamAddr);");
            sb.AppendLine("    string line3 = string.Format(\"CPU:{0} NMI:{1}/{2} A:{3:X2} X:{4:X2} Y:{5:X2} SP:{6:X2} P:{7:X2}\", Runtime.CpuState, Runtime.NmiPending ? 1 : 0, Runtime.InNmi ? 1 : 0, Runtime.A, Runtime.X, Runtime.Y, Runtime.SP, Runtime.P);");
            sb.AppendLine();
            sb.AppendLine("    e.Graphics.FillRectangle(Brushes.Black, 0, 0, 256, 48);");
            sb.AppendLine("    e.Graphics.DrawString(line1, SystemFonts.DefaultFont, Brushes.Yellow, 2, 2);");
            sb.AppendLine("    e.Graphics.DrawString(line2, SystemFonts.DefaultFont, Brushes.Yellow, 2, 16);");
            sb.AppendLine("    e.Graphics.DrawString(line3, SystemFonts.DefaultFont, Brushes.Yellow, 2, 30);");
            sb.AppendLine();
            sb.AppendLine("    if (!string.IsNullOrEmpty(Runtime.LastError))");
            sb.AppendLine("    {");
            sb.AppendLine("        string msg = Runtime.LastError;");
            sb.AppendLine("        if (msg.Length > 56) msg = msg.Substring(0, 56);");
            sb.AppendLine();
            sb.AppendLine("        e.Graphics.FillRectangle(Brushes.Black, 0, 222, 256, 18);");
            sb.AppendLine("        e.Graphics.DrawString(msg, SystemFonts.DefaultFont, Brushes.Red, 2, 224);");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            sb.AppendLine("static void SetJoyKey(Keys k, bool down)");
            sb.AppendLine("{");
            sb.AppendLine("    int b = KeyBit(k);");
            sb.AppendLine("    if (b < 0) return;");
            sb.AppendLine("    if (down) Runtime.Joy1Buttons = (byte)(Runtime.Joy1Buttons | (1 << b));");
            sb.AppendLine("    else Runtime.Joy1Buttons = (byte)(Runtime.Joy1Buttons & ~(1 << b));");
            sb.AppendLine("}");
            sb.AppendLine("static int KeyBit(Keys k)");
            sb.AppendLine("{");
            sb.AppendLine("    switch (k)");
            sb.AppendLine("    {");
            sb.AppendLine("        case Keys.X: return 0;");
            sb.AppendLine("        case Keys.Z: return 1;");
            sb.AppendLine("        case Keys.Back: case Keys.ShiftKey: case Keys.LShiftKey: case Keys.RShiftKey: return 2;");
            sb.AppendLine("        case Keys.Enter: return 3;");
            sb.AppendLine("        case Keys.Up: return 4;");
            sb.AppendLine("        case Keys.Down: return 5;");
            sb.AppendLine("        case Keys.Left: return 6;");
            sb.AppendLine("        case Keys.Right: return 7;");
            sb.AppendLine("        default: return -1;");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine("protected override void OnFormClosing(FormClosingEventArgs e)");
            sb.AppendLine("{");
            sb.AppendLine("    base.OnFormClosing(e);");
            sb.AppendLine("    _timer.Stop();");
            sb.AppendLine("    Audio.TryStop();");
            sb.AppendLine();
            sb.AppendLine("    if (_back != null)");
            sb.AppendLine("    {");
            sb.AppendLine("        _back.Dispose();");
            sb.AppendLine("        _back = null;");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            sb.AppendLine("}");
        }

        void EmitInstruction(StringBuilder sb, Instruction inst)
        {
            if (inst.Control == OpControl.Invalid)
            {
                Line(sb, "Runtime.UnknownOpcode(0x" + inst.Opcode.ToString("X2") + ", 0x" + inst.Address.ToString("X4") + ");");
                EmitGoto(sb, inst.Fallthrough);
                return;
            }

            switch (inst.Control)
            {
                case OpControl.Branch:
                    EmitBranch(sb, inst);
                    return;

                case OpControl.Jmp:
                    EmitGoto(sb, inst.Target);
                    return;

                case OpControl.JmpInd:
                    EmitIndirectJump(sb, inst);
                    return;

                case OpControl.Jsr:
                    EmitJsr(sb, inst);
                    return;

                case OpControl.Rts:
                    EmitRts(sb);
                    return;

                case OpControl.Rti:
                    EmitRti(sb);
                    return;

                case OpControl.Brk:
                    EmitBrk(sb, inst);
                    return;
            }

            EmitOperation(sb, inst);

            if (inst.Info != null && inst.Info.Mn == "KIL")
                return;

            EmitGoto(sb, inst.Fallthrough);
        }

        void EmitBranch(StringBuilder sb, Instruction inst)
        {
            string cond = GetBranchCondition(inst.Info.Mn);

            Line(sb, "if (" + cond + ")");
            Line(sb, "{");

            if (_labels.ContainsKey(inst.Target))
            {
                EmitGoto(sb, inst.Target);
            }
            else
            {
                Line(sb, "Runtime.Trap(\"Missing branch target $" + inst.Target.ToString("X4") + ", using fallthrough\");");
                EmitGoto(sb, inst.Fallthrough);
            }

            Line(sb, "}");
            Line(sb, "else");
            Line(sb, "{");

            EmitGoto(sb, inst.Fallthrough);

            Line(sb, "}");
        }

        void EmitJsr(StringBuilder sb, Instruction inst)
        {
            Line(sb, "Runtime.JsrPush(0x" + inst.Fallthrough.ToString("X4") + ");");
            EmitGoto(sb, inst.Target);
        }

        void EmitRts(StringBuilder sb)
        {
            Line(sb, "Runtime.DispatchTarget = Runtime.RtsPull();");
            Line(sb, "goto Dispatch;");
        }

        void EmitRti(StringBuilder sb)
        {
            Line(sb, "Runtime.P = (byte)((Runtime.Pull() & 0xEF) | 0x20);");
            Line(sb, "Runtime.DispatchTarget = Runtime.Pull16();");
            Line(sb, "Runtime.InNmi = false;");
            Line(sb, "goto Dispatch;");
        }

        void EmitBrk(StringBuilder sb, Instruction inst)
        {
            ushort pcPlus2 = (ushort)(inst.Address + 2);
            Line(sb, "Runtime.Push16(0x" + pcPlus2.ToString("X4") + ");");
            Line(sb, "Runtime.Push((byte)(Runtime.P | 0x30));");
            Line(sb, "Runtime.P = (byte)(Runtime.P | 0x04);");
            Line(sb, "Runtime.DispatchTarget = Memory.Read16(0xFFFE);");
            Line(sb, "goto Dispatch;");
        }

        void EmitIndirectJump(StringBuilder sb, Instruction inst)
        {
            Line(sb, "Runtime.DispatchTarget = Memory.Read16(0x" + inst.Operand.ToString("X4") + ");");
            Line(sb, "goto Dispatch;");
        }

        void EmitDispatchSwitch(StringBuilder sb, string var)
        {
            EmitDispatchSwitch(sb, var, _dispatch);
        }

        void EmitDispatchSwitch(StringBuilder sb, string var, List<ushort> labels)
        {
            Line(sb, "switch (" + var + ")");
            Line(sb, "{");

            foreach (ushort addr in labels)
            {
                Line(sb, "case 0x" + addr.ToString("X4") + ": goto L" + addr.ToString("X4") + ";");
            }

            Line(sb, "default: Runtime.ReportDynamicTarget(" + var + "); Runtime.Trap(\"Dynamic target $\" + " + var + ".ToString(\"X4\")); return;");
            Line(sb, "}");
        }

        void EmitGoto(StringBuilder sb, ushort addr)
        {
            if (_labels.ContainsKey(addr))
            {
                Line(sb, "goto L" + addr.ToString("X4") + ";");
            }
            else
            {
                Line(sb, "Runtime.DispatchTarget = 0x" + addr.ToString("X4") + ";");
                Line(sb, "goto Dispatch;");
            }
        }

        void EmitOperation(StringBuilder sb, Instruction inst)
        {
            if (inst.Info == null)
            {
                Line(sb, "Runtime.UnknownOpcode(0x" + inst.Opcode.ToString("X2") + ", 0x" + inst.Address.ToString("X4") + ");");
                return;
            }

            string mn = inst.Info.Mn;
            AddrMode mode = inst.Info.Mode;
            string read = GetReadExpr(inst);
            string addrExpr = GetAddressExpr(inst);

            switch (mn)
            {
                case "LDA":
                    Line(sb, "Runtime.A = " + read + "; Runtime.SetNZ(Runtime.A);");
                    break;

                case "LDX":
                    Line(sb, "Runtime.X = " + read + "; Runtime.SetNZ(Runtime.X);");
                    break;

                case "LDY":
                    Line(sb, "Runtime.Y = " + read + "; Runtime.SetNZ(Runtime.Y);");
                    break;

                case "STA":
                    Line(sb, "Memory.Write(" + addrExpr + ", Runtime.A);");
                    break;

                case "STX":
                    Line(sb, "Memory.Write(" + addrExpr + ", Runtime.X);");
                    break;

                case "STY":
                    Line(sb, "Memory.Write(" + addrExpr + ", Runtime.Y);");
                    break;

                case "TAX":
                    Line(sb, "Runtime.X = Runtime.A; Runtime.SetNZ(Runtime.X);");
                    break;

                case "TAY":
                    Line(sb, "Runtime.Y = Runtime.A; Runtime.SetNZ(Runtime.Y);");
                    break;

                case "TXA":
                    Line(sb, "Runtime.A = Runtime.X; Runtime.SetNZ(Runtime.A);");
                    break;

                case "TYA":
                    Line(sb, "Runtime.A = Runtime.Y; Runtime.SetNZ(Runtime.A);");
                    break;

                case "TXS":
                    Line(sb, "Runtime.SP = Runtime.X;");
                    break;

                case "TSX":
                    Line(sb, "Runtime.X = Runtime.SP; Runtime.SetNZ(Runtime.X);");
                    break;

                case "INX":
                    Line(sb, "Runtime.X = (byte)(Runtime.X + 1); Runtime.SetNZ(Runtime.X);");
                    break;

                case "INY":
                    Line(sb, "Runtime.Y = (byte)(Runtime.Y + 1); Runtime.SetNZ(Runtime.Y);");
                    break;

                case "DEX":
                    Line(sb, "Runtime.X = (byte)(Runtime.X - 1); Runtime.SetNZ(Runtime.X);");
                    break;

                case "DEY":
                    Line(sb, "Runtime.Y = (byte)(Runtime.Y - 1); Runtime.SetNZ(Runtime.Y);");
                    break;

                case "ADC":
                    Line(sb, "Runtime.Adc(" + read + ");");
                    break;

                case "SBC":
                    Line(sb, "Runtime.Sbc(" + read + ");");
                    break;

                case "CMP":
                    Line(sb, "Runtime.Compare(Runtime.A, " + read + ");");
                    break;

                case "CPX":
                    Line(sb, "Runtime.Compare(Runtime.X, " + read + ");");
                    break;

                case "CPY":
                    Line(sb, "Runtime.Compare(Runtime.Y, " + read + ");");
                    break;

                case "AND":
                    Line(sb, "Runtime.A = (byte)(Runtime.A & " + read + "); Runtime.SetNZ(Runtime.A);");
                    break;

                case "ORA":
                    Line(sb, "Runtime.A = (byte)(Runtime.A | " + read + "); Runtime.SetNZ(Runtime.A);");
                    break;

                case "EOR":
                    Line(sb, "Runtime.A = (byte)(Runtime.A ^ " + read + "); Runtime.SetNZ(Runtime.A);");
                    break;

                case "BIT":
                    Line(sb, "Runtime.Bit(" + read + ");");
                    break;

                case "ASL":
                    if (mode == AddrMode.Acc)
                        Line(sb, "Runtime.A = Runtime.Asl(Runtime.A);");
                    else
                        EmitRmw(sb, addrExpr, "Asl");
                    break;

                case "LSR":
                    if (mode == AddrMode.Acc)
                        Line(sb, "Runtime.A = Runtime.Lsr(Runtime.A);");
                    else
                        EmitRmw(sb, addrExpr, "Lsr");
                    break;

                case "ROL":
                    if (mode == AddrMode.Acc)
                        Line(sb, "Runtime.A = Runtime.Rol(Runtime.A);");
                    else
                        EmitRmw(sb, addrExpr, "Rol");
                    break;

                case "ROR":
                    if (mode == AddrMode.Acc)
                        Line(sb, "Runtime.A = Runtime.Ror(Runtime.A);");
                    else
                        EmitRmw(sb, addrExpr, "Ror");
                    break;

                case "INC":
                    EmitIncDec(sb, addrExpr, true);
                    break;

                case "DEC":
                    EmitIncDec(sb, addrExpr, false);
                    break;

                case "CLC":
                    Line(sb, "Runtime.P = (byte)(Runtime.P & 0xFE);");
                    break;

                case "SEC":
                    Line(sb, "Runtime.P = (byte)(Runtime.P | 0x01);");
                    break;

                case "CLI":
                    Line(sb, "Runtime.P = (byte)(Runtime.P & 0xFB);");
                    break;

                case "SEI":
                    Line(sb, "Runtime.P = (byte)(Runtime.P | 0x04);");
                    break;

                case "CLV":
                    Line(sb, "Runtime.P = (byte)(Runtime.P & 0xBF);");
                    break;

                case "CLD":
                    Line(sb, "Runtime.P = (byte)(Runtime.P & 0xF7);");
                    break;

                case "SED":
                    Line(sb, "Runtime.P = (byte)(Runtime.P | 0x08);");
                    break;

                case "PHA":
                    Line(sb, "Runtime.Push(Runtime.A);");
                    break;

                case "PLA":
                    Line(sb, "Runtime.A = Runtime.Pull(); Runtime.SetNZ(Runtime.A);");
                    break;

                case "PHP":
                    Line(sb, "Runtime.Push((byte)(Runtime.P | 0x30));");
                    break;

                case "PLP":
                    Line(sb, "Runtime.P = (byte)((Runtime.Pull() & 0xEF) | 0x20);");
                    break;

                case "NOP":
                    break;

                case "LAX":
                    Line(sb, "Runtime.A = Runtime.X = " + read + "; Runtime.SetNZ(Runtime.A);");
                    break;

                case "SAX":
                    Line(sb, "Memory.Write(" + addrExpr + ", (byte)(Runtime.A & Runtime.X));");
                    break;

                case "DCP":
                    Line(sb, "ushort rmw = " + addrExpr + ";");
                    Line(sb, "byte tmp = (byte)(Memory.Read(rmw) - 1);");
                    Line(sb, "Memory.Write(rmw, tmp);");
                    Line(sb, "Runtime.Compare(Runtime.A, tmp);");
                    break;

                case "ISB":
                    Line(sb, "ushort rmw = " + addrExpr + ";");
                    Line(sb, "byte tmp = (byte)(Memory.Read(rmw) + 1);");
                    Line(sb, "Memory.Write(rmw, tmp);");
                    Line(sb, "Runtime.Sbc(tmp);");
                    break;

                case "SLO":
                    Line(sb, "ushort rmw = " + addrExpr + ";");
                    Line(sb, "byte tmp = Runtime.Asl(Memory.Read(rmw));");
                    Line(sb, "Memory.Write(rmw, tmp);");
                    Line(sb, "Runtime.A = (byte)(Runtime.A | tmp); Runtime.SetNZ(Runtime.A);");
                    break;

                case "RLA":
                    Line(sb, "ushort rmw = " + addrExpr + ";");
                    Line(sb, "byte tmp = Runtime.Rol(Memory.Read(rmw));");
                    Line(sb, "Memory.Write(rmw, tmp);");
                    Line(sb, "Runtime.A = (byte)(Runtime.A & tmp); Runtime.SetNZ(Runtime.A);");
                    break;

                case "SRE":
                    Line(sb, "ushort rmw = " + addrExpr + ";");
                    Line(sb, "byte tmp = Runtime.Lsr(Memory.Read(rmw));");
                    Line(sb, "Memory.Write(rmw, tmp);");
                    Line(sb, "Runtime.A = (byte)(Runtime.A ^ tmp); Runtime.SetNZ(Runtime.A);");
                    break;

                case "RRA":
                    Line(sb, "ushort rmw = " + addrExpr + ";");
                    Line(sb, "byte tmp = Runtime.Ror(Memory.Read(rmw));");
                    Line(sb, "Memory.Write(rmw, tmp);");
                    Line(sb, "Runtime.Adc(tmp);");
                    break;

                case "AXA":
                    // Неофициальный AXA/AHX.
                    // Точное значение на реальном NES зависит от странной логики,
                    // для заглушки используем A & X.
                    Line(sb, "Memory.Write(" + addrExpr + ", (byte)(Runtime.A & Runtime.X));");
                    break;

                case "KIL":
                    Line(sb, "Runtime.Trap(\"KIL/HLT opcode executed\"); return;");
                    break;

                case "ANC":
                    Line(sb, "Runtime.A = (byte)(Runtime.A & " + read + "); Runtime.SetCarry((Runtime.A & 0x80) != 0); Runtime.SetNZ(Runtime.A);");
                    break;

                case "ALR":
                    Line(sb, "Runtime.A = Runtime.Lsr((byte)(Runtime.A & " + read + "));");
                    break;

                case "ARR":
                    Line(sb, "Runtime.A = Runtime.Ror((byte)(Runtime.A & " + read + "));");
                    break;

                case "ANE":
                    Line(sb, "Runtime.A = (byte)((Runtime.A | 0xEE) & Runtime.X & " + read + "); Runtime.SetNZ(Runtime.A);");
                    break;

                case "LXA":
                    Line(sb, "Runtime.A = (byte)((Runtime.A | 0xEE) & " + read + "); Runtime.X = Runtime.A; Runtime.SetNZ(Runtime.A);");
                    break;

                case "AXS":
                    Line(sb, "{ int axs = (Runtime.A & Runtime.X) - " + read + "; Runtime.X = (byte)axs; Runtime.SetCarry(axs >= 0); Runtime.SetNZ(Runtime.X); }");
                    break;

                case "SHY":
                    Line(sb, "Memory.Write(" + addrExpr + ", Runtime.Y);");
                    break;

                case "LAS":
                    Line(sb, "Runtime.SP = (byte)(Runtime.SP & " + read + "); Runtime.A = Runtime.SP; Runtime.X = Runtime.SP; Runtime.SetNZ(Runtime.SP);");
                    break;

                default:
                    Line(sb, "Runtime.UnknownOpcode(0x" + inst.Opcode.ToString("X2") + ", 0x" + inst.Address.ToString("X4") + ");");
                    break;
            }
        }

        void EmitRmw(StringBuilder sb, string addrExpr, string helper)
        {
            Line(sb, "ushort rmw = " + addrExpr + ";");
            Line(sb, "byte tmp = Memory.Read(rmw);");
            Line(sb, "tmp = Runtime." + helper + "(tmp);");
            Line(sb, "Memory.Write(rmw, tmp);");
        }

        void EmitIncDec(StringBuilder sb, string addrExpr, bool inc)
        {
            Line(sb, "ushort rmw = " + addrExpr + ";");
            if (inc)
                Line(sb, "byte tmp = (byte)(Memory.Read(rmw) + 1);");
            else
                Line(sb, "byte tmp = (byte)(Memory.Read(rmw) - 1);");
            Line(sb, "Runtime.SetNZ(tmp);");
            Line(sb, "Memory.Write(rmw, tmp);");
        }

        string GetBranchCondition(string mn)
        {
            switch (mn)
            {
                case "BPL": return "(Runtime.P & 0x80) == 0";
                case "BMI": return "(Runtime.P & 0x80) != 0";
                case "BVC": return "(Runtime.P & 0x40) == 0";
                case "BVS": return "(Runtime.P & 0x40) != 0";
                case "BCC": return "(Runtime.P & 0x01) == 0";
                case "BCS": return "(Runtime.P & 0x01) != 0";
                case "BNE": return "(Runtime.P & 0x02) == 0";
                case "BEQ": return "(Runtime.P & 0x02) != 0";
                default: return "true";
            }
        }

        string GetReadExpr(Instruction inst)
        {
            switch (inst.Info.Mode)
            {
                case AddrMode.Imm:
                    return "0x" + inst.Operand.ToString("X2");

                case AddrMode.Acc:
                    return "Runtime.A";

                case AddrMode.Imp:
                    return "0";

                default:
                    return "Memory.Read(" + GetAddressExpr(inst) + ")";
            }
        }

        string GetAddressExpr(Instruction inst)
        {
            string op2 = inst.Operand.ToString("X2");
            string op4 = inst.Operand.ToString("X4");

            switch (inst.Info.Mode)
            {
                case AddrMode.Zp:
                    return "0x" + op2;

                case AddrMode.ZpX:
                    return "(ushort)((0x" + op2 + " + Runtime.X) & 0xFF)";

                case AddrMode.ZpY:
                    return "(ushort)((0x" + op2 + " + Runtime.Y) & 0xFF)";

                case AddrMode.Abs:
                    return "0x" + op4;

                case AddrMode.AbsX:
                    return "unchecked((ushort)(0x" + op4 + " + Runtime.X))";

                case AddrMode.AbsY:
                    return "unchecked((ushort)(0x" + op4 + " + Runtime.Y))";

                case AddrMode.XInd:
                    return "Runtime.AddrXInd(0x" + op2 + ")";

                case AddrMode.IndY:
                    return "Runtime.AddrIndY(0x" + op2 + ")";

                default:
                    return "0";
            }
        }

        int MnemIdFor(OpInfo info)
        {
            if (info == null) return 0;
            switch (info.Ctrl)
            {
                case OpControl.Jmp: return 44;
                case OpControl.JmpInd: return 44;
                case OpControl.Jsr: return 45;
                case OpControl.Rts: return 46;
                case OpControl.Rti: return 47;
                case OpControl.Brk: return 48;
                case OpControl.Branch: return 49;
                case OpControl.Invalid: return 0;
            }
            switch (info.Mn)
            {
                case "LDA": return 1;
                case "LDX": return 2;
                case "LDY": return 3;
                case "STA": return 4;
                case "STX": return 5;
                case "STY": return 6;
                case "TAX": return 7;
                case "TAY": return 8;
                case "TXA": return 9;
                case "TYA": return 10;
                case "TXS": return 11;
                case "TSX": return 12;
                case "INX": return 13;
                case "INY": return 14;
                case "DEX": return 15;
                case "DEY": return 16;
                case "ADC": return 17;
                case "SBC": return 18;
                case "CMP": return 19;
                case "CPX": return 20;
                case "CPY": return 21;
                case "AND": return 22;
                case "ORA": return 23;
                case "EOR": return 24;
                case "BIT": return 25;
                case "ASL": return 26;
                case "LSR": return 27;
                case "ROL": return 28;
                case "ROR": return 29;
                case "INC": return 30;
                case "DEC": return 31;
                case "CLC": return 32;
                case "SEC": return 33;
                case "CLI": return 34;
                case "SEI": return 35;
                case "CLV": return 36;
                case "CLD": return 37;
                case "SED": return 38;
                case "PHA": return 39;
                case "PLA": return 40;
                case "PHP": return 41;
                case "PLP": return 42;
                case "NOP": return 43;
                case "LAX": return 50;
                case "SAX": return 51;
                case "DCP": return 52;
                case "ISB": return 53;
                case "SLO": return 54;
                case "RLA": return 55;
                case "SRE": return 56;
                case "RRA": return 57;
                case "ANC": return 58;
                case "ALR": return 59;
                case "ARR": return 60;
                case "ANE": return 61;
                case "LXA": return 62;
                case "AXS": return 63;
                case "SHY": return 64;
                case "LAS": return 65;
                case "AXA": return 66;
                case "KIL": return 67;
                default: return 0;
            }
        }
        void Line(StringBuilder sb, string text)
        {
            sb.AppendLine("    " + text);
        }
    }

    public static class PointerScanner
    {
        public static List<ushort> ScanZpPointerTargets(NesRom rom, byte zp)
        {
            List<ushort> result = new List<ushort>();

            byte[] prg = rom.PrgRom;

            byte? currentImmediate = null;
            int currentImmediatePos = -1000000;

            byte? low = null;
            byte? high = null;

            int lowPos = -1000000;
            int highPos = -1000000;

            const int window = 12;
            const int maxTargets = 32;

            int i = 0;

            while (i < prg.Length)
            {
                byte op = prg[i];
                OpInfo info = Cpu6502.Table[op];

                int len = info == null ? 1 : info.Len;

                if (info != null && i + len <= prg.Length)
                {
                    if ((info.Mn == "LDA" || info.Mn == "LDX" || info.Mn == "LDY") &&
                        info.Mode == AddrMode.Imm)
                    {
                        currentImmediate = prg[i + 1];
                        currentImmediatePos = i;
                    }
                    else if ((info.Mn == "STA" || info.Mn == "STX" || info.Mn == "STY") &&
                             info.Mode == AddrMode.Zp &&
                             currentImmediate.HasValue &&
                             (i - currentImmediatePos) <= window)
                    {
                        byte dest = prg[i + 1];

                        if (dest == zp)
                        {
                            low = currentImmediate;
                            lowPos = i;

                            if (high.HasValue && Math.Abs(highPos - i) <= window)
                            {
                                AddTarget(result, high.Value, low.Value);
                            }
                        }
                        else if (dest == (byte)(zp + 1))
                        {
                            high = currentImmediate;
                            highPos = i;

                            if (low.HasValue && Math.Abs(i - lowPos) <= window)
                            {
                                AddTarget(result, high.Value, low.Value);
                            }
                        }
                    }
                }

                i += len;

                if (result.Count >= maxTargets)
                    break;
            }

            return result;
        }

        static void AddTarget(List<ushort> result, byte hi, byte lo)
        {
            ushort target = (ushort)(lo | (hi << 8));

            if (target >= 0x8000 && target < 0xFFFA)
            {
                if (!result.Contains(target))
                    result.Add(target);
            }
        }
    }
}