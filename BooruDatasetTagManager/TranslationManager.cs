using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Translator.Crypto;
using static System.Net.Mime.MediaTypeNames;

namespace BooruDatasetTagManager
{
    public class TranslationManager
    {
        private string _language;
        private string _workDir;
        public List<TransItem> Translations { get; set; }
        private AbstractTranslator translator;
        private string translationFilePath;
        private HashSet<long> _hashSet;
        private bool _offlineMode;

        public TranslationManager(string toLang, TranslationService service, string workDir, bool offlineMode = false, string customTranslationFile = "")
        {
            _language = toLang;
            _workDir = workDir;
            Translations = new List<TransItem>();
            _hashSet = new HashSet<long>();
            _offlineMode = offlineMode;
            translator = AbstractTranslator.Create(service);
            if (!string.IsNullOrEmpty(customTranslationFile) && File.Exists(customTranslationFile))
            {
                translationFilePath = customTranslationFile;
            }
            else
            {
                translationFilePath = Path.Combine(_workDir, _language + ".txt");
            }
        }

        public void LoadTranslations()
        {
            if (!File.Exists(translationFilePath))
            {
                var sw = File.CreateText(translationFilePath);
                sw.WriteLine("//Translation format: <original>=<translation>");
                sw.Dispose();
                return;
            }
            string[] lines = File.ReadAllLines(translationFilePath);
            foreach (var item in lines)
            {
                if (item.Trim().StartsWith("//"))
                    continue;
                var transItem = TransItem.Create(item);
                if (transItem != null && !_hashSet.Contains(transItem.OrigHash))
                {
                    Translations.Add(transItem);
                    _hashSet.Add(transItem.OrigHash);
                }
            }
        }

        public bool Contains(string orig)
        {
            return _hashSet.Contains(orig.ToLower().GetHash());
        }

        public bool Contains(long hash)
        {
            return _hashSet.Contains(hash);
        }

        public string GetTranslation(string text)
        {
            return GetTranslation(text.ToLower().Trim().GetHash());
        }

        public string GetTranslation(long hash)
        {
            var res = Translations.FirstOrDefault(a => a.OrigHash == hash);
            if (res == null)
                return null;
            return res.Trans;
        }

        public string GetTranslation(long hash, bool onlyManual)
        {
            if (onlyManual)
            {
                var res = Translations.FirstOrDefault(a => a.OrigHash == hash && a.IsManual == onlyManual);
                if (res == null)
                    return null;
                return res.Trans;
            }
            else
                return GetTranslation(hash);

        }

        public async Task<string> GetTranslationAsync(string text)
        {
            return await Task.Run(() =>
            {
                return GetTranslation(text);
            });
        }
        public void AddTranslation(string orig, string trans, bool isManual)
        {
            File.AppendAllText(translationFilePath, $"{orig}={trans}\r\n", Encoding.UTF8);
            var newItem = new TransItem(orig, trans, isManual);
            Translations.Add(newItem);
            _hashSet.Add(newItem.OrigHash);
        }

        public async Task AddTranslationAsync(string orig, string trans, bool isManual)
        {
            StreamWriter sw = new StreamWriter(translationFilePath, true, Encoding.UTF8);
            await sw.WriteLineAsync($"{(isManual ? "*" : "")}{orig}={trans}");
            sw.Close();
            var newItem = new TransItem(orig, trans, isManual);
            Translations.Add(newItem);
            _hashSet.Add(newItem.OrigHash);
        }

        public async Task<string> TranslateAsync(string text)
        {
            await Program.TranslationLocker.WaitAsync();
            if (string.IsNullOrWhiteSpace(text))
            {
                Program.TranslationLocker.Release();
                return string.Empty;
            }
            string result = await GetTranslationAsync(text);
            if (result != null)
            {
                Program.TranslationLocker.Release();
                return result;
            }
            
            if (_offlineMode)
            {
                Program.TranslationLocker.Release();
                return string.Empty;
            }
            
            result = await translator.TranslateAsync(text, "en", _language);
            if (!string.IsNullOrEmpty(result))
                await AddTranslationAsync(text, result, false);
            Program.TranslationLocker.Release();
            return result;
        }


        public class TransItem
        {
            public string Orig { get; private set; }
            public string Trans {get; set; }
            public long OrigHash { get; private set; }
            public bool IsManual { get; private set; }

            public TransItem(string orig, string trans, bool isManual)
            {
                Orig = orig;
                Trans = trans;
                OrigHash = orig.ToLower().GetHash();
                IsManual = isManual;
            }

            public static TransItem Create(string text)
            {
                bool manual = false;
                if (text.StartsWith("*"))
                {
                    text = text.Substring(1);
                    manual = true;
                }
                int index = text.LastIndexOf('=');
                if (index == -1)
                    return null;
                string orig = text.Substring(0, index).Trim();
                string trans = text.Substring(index + 1).Trim();
                return new TransItem(orig, trans, manual);
            }


            public TransItem() { }
        }
    }
}
