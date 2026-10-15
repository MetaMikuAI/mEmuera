using mEmuera.Contracts;

namespace mEmuera.EraCompat.Loading;

internal static class EngineConfig
{
    public static CompileOptions Apply(SourceText source, CompileOptions options)
    {
        options = ConfigQueryMetadata.Initialize(options);
        int number = 0, offset = 0;
        foreach (string line in source.Text.Split('\n'))
        {
            number++; var span = new SourceSpan(source.Id, offset, line.Length, number); offset += line.Length + 1;
            int colon = line.IndexOf(':'); if (colon < 0 || line.TrimStart().StartsWith(';')) continue;
            string key = line[..colon].Trim().ToUpperInvariant(), value = line[(colon + 1)..].Trim();
            key = ConfigQueryMetadata.ConfigurationKey(key);
            bool enabled = value.Equals("YES", StringComparison.OrdinalIgnoreCase) || value.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
                || int.TryParse(value, out int flag) && flag != 0 || value == "前";
            int Color()
            {
                var components = value.Split(',');
                if (components.Length != 3 || components.Any(c => !byte.TryParse(c.Trim(), out _))) throw new ScriptException("CONFIG_COLOR", "颜色需要三个 0 至 255 的数值", span);
                return byte.Parse(components[0].Trim()) << 16 | byte.Parse(components[1].Trim()) << 8 | byte.Parse(components[2].Trim());
            }
            options = key switch
            {
                "文字色" or "TEXT COLOR" => options with { DefaultForeground = Color() },
                "選択中文字色" or "HIGHLIGHT COLOR" => options with { FocusColor = Color() },
                "背景色" or "BACKGROUND COLOR" => options with { DefaultBackground = Color() },
                "FONT" or "FONT NAME" or "フォント名" => options with { DefaultFont = value },
                "AUTO TOSTR CONVERSION FOR USER FUNCTION ARGUMENTS" or "ユーザー関数の引数に自動的にTOSTRを補完する" => options with { AutoStringArguments = enabled },
                "ALLOW SP CHARACTERS" or "SPキャラを使用する" => options with { AllowSpCharacters = enabled },
                "NUMBER OF ITEM CHARACTERS FOR PRINTC" or "PRINTCの文字数" => options with { PrintColumnWidth = int.TryParse(value, out int width) && width is >= 0 and <= 10000 ? width : throw new ScriptException("CONFIG_PRINTC", "列宽配置越界", span) },
                "ITEMS PER LINE FOR PRINTC" or "PRINTCを並べる数" => options with { PrintColumnsPerLine = int.TryParse(value, out int columns) && columns >= 0 ? columns : throw new ScriptException("CONFIG_PRINTC", "列数配置越界", span) },
                "ALLOW CALL ON EVENT FUNCTIONS" or "イベント関数のCALLを許可する" => options with { AllowCallEvent = enabled },
                "ALLOW ARGUMENTS OMISSION FOR USER FUNCTIONS" or "ユーザー関数の全ての引数の省略を許可する" => options with { CompatiFuncArgOptional = enabled },
                "IMITATE BEHAVIOR FOR TIMES" or "TIMESの計算をERAMAKERにあわせる" => options with { LegacyTimes = enabled },
                "DISABLE BEFORE_ERROR/THROW EVENTS" or "BEFORE_ERROR/THROWイベントを無効化する" => options with { DisableBeforeErrorThrow = enabled },
                "SEARCH SUBFOLDERS" or "サブディレクトリを検索する" => options with { SearchSubdirectories = enabled },
                "SORT FILENAMES" or "読み込み順をファイル名順にソートする" => options with { SortFiles = enabled },
                "USE _RENAME.CSV FILE" or "_RENAME.CSVを利用する" => options with { UseRename = enabled },
                "USE _REPLACE.CSV FILE" or "_REPLACE.CSVを利用する" => options with { UseReplace = enabled },
                "USE ERD" or "ERD機能を利用する" => options with { UseErd = enabled },
                "FPS" or "フレーム毎秒" => options with { FramesPerSecond = int.TryParse(value, out int fps) && fps is >= 1 and <= 240 ? fps : throw new ScriptException("CONFIG_FPS", "FPS 需要 1 至 240 的整数", span) },
                "VALID EXTENSIONS FOR LOADTEXT AND SAVETEXT" or "LOADTEXTとSAVETEXTで使える拡張子" => options with { TextExtensions = [.. value.Split(',').Select(s => s.Trim().ToLowerInvariant())] },
                "USE SAVE FOLDER" or "USE SAV FOLDER" or "セーブデータをsavフォルダに保存する" => options with { UseSaveFolder = enabled },
                "DEFAULT ANSI ENCODING" => options with { LanguageCodePage = value.ToUpperInvariant() switch
                    { "JAPANESE" => 932, "KOREAN" => 949, "CHINESE_HANS" => 936, "CHINESE_HANT" => 950, _ => throw new ScriptException("CONFIG_ENCODING", "ANSI 语言配置无效", span) } },
                _ => options
            };
            options = ConfigQueryMetadata.Apply(options, key, value);
            if (key == "IGNORE CASE" && !enabled || key == "DO NOT IGNORE CASE FOR FUNCTIONS AND ATTRIBUTES" && enabled)
                throw new ScriptException("CONFIG_UNSUPPORTED", "尚未支持区分大小写的标识符配置", span);
        }
        return ConfigQueryMetadata.Sync(options);
    }
}
