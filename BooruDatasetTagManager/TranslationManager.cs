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
        private Dictionary<long, string> _translationDict;
        private bool _offlineMode;
        private bool _isCsvFormat;

        public TranslationManager(string toLang, TranslationService service, string workDir, bool offlineMode = false, string customTranslationFile = "")
        {
            _language = toLang;
            _workDir = workDir;
            Translations = new List<TransItem>();
            _hashSet = new HashSet<long>();
            _translationDict = new Dictionary<long, string>();
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
            _isCsvFormat = Path.GetExtension(translationFilePath).Equals(".csv", StringComparison.OrdinalIgnoreCase);
        }

        public void LoadTranslations()
        {
            if (!File.Exists(translationFilePath))
            {
                var sw = File.CreateText(translationFilePath);
                if (_isCsvFormat)
                {
                    sw.WriteLine("//Translation format: <original>,<translation>");
                }
                else
                {
                    sw.WriteLine("//Translation format: <original>=<translation>");
                }
                sw.Dispose();
                return;
            }
            string[] lines = File.ReadAllLines(translationFilePath);
            foreach (var item in lines)
            {
                if (item.Trim().StartsWith("//"))
                    continue;
                var transItem = TransItem.Create(item, _isCsvFormat);
                if (transItem != null && !_hashSet.Contains(transItem.OrigHash))
                {
                    Translations.Add(transItem);
                    _hashSet.Add(transItem.OrigHash);
                    _translationDict[transItem.OrigHash] = transItem.Trans;
                }
            }
        }

        public bool Contains(string orig)
        {
            if (_isCsvFormat)
            {
                orig = orig.Replace(" ", "_");
            }
            return _hashSet.Contains(orig.ToLower().GetHash());
        }

        public bool Contains(long hash)
        {
            return _hashSet.Contains(hash);
        }

        public string GetTranslation(string text)
        {
            if (_isCsvFormat)
            {
                text = text.Replace(" ", "_");
            }
            return GetTranslation(text.ToLower().Trim().GetHash());
        }

        public string GetTranslation(long hash)
        {
            string result;
            if (_translationDict.TryGetValue(hash, out result))
            {
                return result;
            }
            return null;
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
            {
                string result;
                if (_translationDict.TryGetValue(hash, out result))
                {
                    return result;
                }
                return null;
            }
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
            if (_isCsvFormat)
            {
                orig = orig.Replace(" ", "_");
            }
            string line;
            if (_isCsvFormat)
            {
                line = $"{(isManual ? "*" : "")}{orig},{trans}";
            }
            else
            {
                line = $"{orig}={trans}";
            }
            File.AppendAllText(translationFilePath, line + "\r\n", Encoding.UTF8);
            var newItem = new TransItem(orig, trans, isManual);
            Translations.Add(newItem);
            _hashSet.Add(newItem.OrigHash);
            _translationDict[newItem.OrigHash] = trans;
        }

        public async Task AddTranslationAsync(string orig, string trans, bool isManual)
        {
            StreamWriter sw = new StreamWriter(translationFilePath, true, Encoding.UTF8);
            string line;
            if (_isCsvFormat)
            {
                line = $"{(isManual ? "*" : "")}{orig},{trans}";
            }
            else
            {
                line = $"{(isManual ? "*" : "")}{orig}={trans}";
            }
            await sw.WriteLineAsync(line);
            sw.Close();
            var newItem = new TransItem(orig, trans, isManual);
            Translations.Add(newItem);
            _hashSet.Add(newItem.OrigHash);
            _translationDict[newItem.OrigHash] = trans;
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
            
            string originalText = text;
            if (_isCsvFormat)
            {
                text = text.Replace(" ", "_");
            }
            result = await translator.TranslateAsync(originalText, "en", _language);
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

            public static TransItem Create(string text, bool isCsvFormat = false)
            {
                bool manual = false;
                if (text.StartsWith("*"))
                {
                    text = text.Substring(1);
                    manual = true;
                }
                int index;
                if (isCsvFormat)
                {
                    index = text.LastIndexOf(',');
                }
                else
                {
                    index = text.LastIndexOf('=');
                }
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
