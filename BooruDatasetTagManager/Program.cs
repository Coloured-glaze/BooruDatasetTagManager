using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;
using System.IO;
using System.Diagnostics;
using System.Threading;
using UmaMusumeDBBrowser;
using System.Reflection;
using System.Runtime.InteropServices;
using BooruDatasetTagManager.AiApi;

namespace BooruDatasetTagManager    
{
    static class Program
    {
        /// <summary>
        /// Главная точка входа для приложения.
        /// </summary>
        [STAThread]
        static async void Main()
        {
            PreloadDotnetDependenciesFromSubdirectoryManually();
            Application.EnableVisualStyles();
#if NET5_0_OR_GREATER
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
#endif
            Application.SetCompatibleTextRenderingDefault(false);
            AppPath = Application.StartupPath;
            Settings = new AppSettings(Application.StartupPath);
            EditableTagListLocker = new SemaphoreSlim(1,1);
            ListChangeLocker = new object();
            TranslationLocker = new SemaphoreSlim(1, 1);
            ColorManager = new ColorSchemeManager();
            ColorManager.Load(Path.Combine(Application.StartupPath, "ColorScheme.json"));
            ColorManager.SelectScheme(Program.Settings.ColorScheme);
            
            I18n.Initialize(Program.Settings.Language);
            Settings.Hotkeys.ChangeLanguage();
            
            await Task.Run(() =>
            {
                string translationsDir = Path.Combine(Application.StartupPath, "Translations");
                if (!Directory.Exists(translationsDir))
                    Directory.CreateDirectory(translationsDir);
                TransManager = new TranslationManager(Program.Settings.TranslationLanguage, Program.Settings.TransService, translationsDir, Program.Settings.OfflineTranslationMode, Program.Settings.TranslationFilePath);
                TransManager.LoadTranslations();
                string tagsDir = Path.Combine(Application.StartupPath, "Tags");
                if(!Directory.Exists(tagsDir))
                    Directory.CreateDirectory(tagsDir);
                string tagFile = Path.Combine(tagsDir, "List.tdb");
                TagsList = TagsDB.LoadFromTagFile(tagFile);
                if (TagsList == null)
                    TagsList = new TagsDB();
                TagsList.LoadTranslation(TransManager);
            });
            AutoTagger = new AiApiClient();
            if (!string.IsNullOrEmpty(Settings.OpenAiAutoTagger.ConnectionAddress) && !string.IsNullOrEmpty(Settings.OpenAiAutoTagger.ApiKey))
            {
                try
                {
                    OpenAiAutoTagger = new AiOpenAiClient(Settings.OpenAiAutoTagger.ConnectionAddress, Settings.OpenAiAutoTagger.ApiKey, Settings.OpenAiAutoTagger.RequestTimeout);
                }
                catch { }
            }

            Application.Run(new MainForm());
        }

        static void PreloadDotnetDependenciesFromSubdirectoryManually()
        {
            // https://www.lostindetails.com/articles/Native-Bindings-in-CSharp
            // https://www.meziantou.net/load-native-libraries-from-a-dynamic-location.htm
            // None of the above worked but approach is inspired by it.
            // First, ensure sub-directory with native libraries is 
            // added to dll directories
            var dllDirectory = Path.Combine(AppContext.BaseDirectory,
                Environment.Is64BitProcess ? "win-x64" : "win-x86");
            var r = AddDllDirectory(dllDirectory);
            Trace.WriteLine($"AddDllDirectory {dllDirectory} {r}");

            // Then, try manually loading the .NET 6 WPF 
            // native library dependencies
            TryManuallyLoad("D3DCompiler_47_cor3");
            TryManuallyLoad("PenImc_cor3");
            TryManuallyLoad("PresentationNative_cor3");
            TryManuallyLoad("vcruntime140_cor3");
            TryManuallyLoad("wpfgfx_cor3");
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern int AddDllDirectory(string NewDirectory);

        static void TryManuallyLoad(string libraryName)
        {
            // NOTE: For the native libraries we load here, 
            //       we do not care about closing the library 
            //       handle since they live as long as the process.
            var loaded = NativeLibrary.TryLoad(libraryName,
                Assembly.GetExecutingAssembly(),
                DllImportSearchPath.SafeDirectories |
                DllImportSearchPath.UserDirectories,
                out var handle);
            if (!loaded)
            {
                Trace.WriteLine($"Failed loading {libraryName}");
            }
            else
            {
                Trace.WriteLine($"Loaded {libraryName}");
            }
        }

        public static string AppPath;

        public static TranslationManager TransManager;

        public static DatasetManager DataManager;

        public static AppSettings Settings;

        public static TagsDB TagsList;

        public static AiApiClient AutoTagger;
        public static AiOpenAiClient OpenAiAutoTagger = null;

        public static ColorSchemeManager ColorManager;

        #region locks
        public static SemaphoreSlim EditableTagListLocker;
        public static object ListChangeLocker;
        public static SemaphoreSlim TranslationLocker;
        public static SemaphoreSlim LoadingLocker = new SemaphoreSlim(1, 1);
        #endregion
    }
}
