using H5.Contract;
using H5.Contract.Constants;
using Microsoft.Extensions.Logging;
using Mono.Cecil;
using Mosaik.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace H5.Translator
{
    public static class HtmlTokens
    {
        public const string META   = "{-!-meta-!-}";
        public const string TITLE  = "{-!-title-!-}";
        public const string CSS    = "{-!-css-!-}";
        public const string SCRIPT = "{-!-script-!-}";
        public const string HEAD   = "{-!-head-!-}";
        public const string BODY   = "{-!-body-!-}";
        
        //The template must match the tokens above!

        public const string TEMPLATE =
@"<!doctype html>
<html lang=en>
<head>
    <meta charset=""utf-8"" />
    {-!-meta-!-}
    <title>{-!-title-!-}</title>
    {-!-css-!-}
    {-!-script-!-}
    {-!-head-!-}
</head>
<body>
{-!-body-!-}
</body>
</html>";
    }

    internal class HtmlGenerator
    {
        private static ILogger Logger = ApplicationLogging.CreateLogger<HtmlGenerator>();

        private readonly IH5DotJson_AssemblySettings _assemblyConfig;

        private readonly TranslatorOutput _translatorOutputs;

        private readonly string _title;

        private readonly string _buildConfig;

        public HtmlGenerator(IH5DotJson_AssemblySettings config, TranslatorOutput outputs, string title, string buildConfig)
        {
            _assemblyConfig = config;
            _translatorOutputs = outputs;
            _title = title;
            _buildConfig = buildConfig;
        }

        public void GenerateHtml(string outputPath)
        {
            string RemapToCDN(string path)
            {
                const string cdn = "https://cdn.jsdelivr.net/npm/@h5.rocks/h5@latest/dist/";
                
                if (_assemblyConfig.Html?.UseCDN ?? false)
                {
                    switch (path)
                    {
                        case "h5.min.js":               return $"{cdn}h5.min.js";
                        case "h5.meta.min.js":          return $"{cdn}h5.meta.min.js";
                        case "newtonsoft.json.min.js":  return $"{cdn}newtonsoft.json.min.js";
                        case "h5.js":                   return $"{cdn}h5.js";
                        case "h5.meta.js":              return $"{cdn}h5.meta.js";
                        case "newtonsoft.json.js":      return $"{cdn}newtonsoft.json.js";
                    }
                }
                return path;
            }

            Logger.LogTrace("GenerateHtml...");
            
            Logger.LogTrace("Applying default html template");

            var htmlTemplate = HtmlTokens.TEMPLATE;

            var indexCss    = htmlTemplate.IndexOf(HtmlTokens.CSS, StringComparison.InvariantCultureIgnoreCase);
            var indexScript = htmlTemplate.IndexOf(HtmlTokens.SCRIPT, StringComparison.InvariantCultureIgnoreCase);

            var cssLinkTemplate = "<link rel=\"stylesheet\" href=\"{0}\">";
            var scriptTemplate = "<script src=\"{0}\" defer></script>";

            var indentCss = GetIndent(htmlTemplate, indexCss);
            var indentScript = GetIndent(htmlTemplate, indexScript);

            var cssBuffer = new StringBuilder();
            var jsBuffer = new StringBuilder();
            var jsMinBuffer = new StringBuilder();

            var outputForHtml = _translatorOutputs.GetOutputs();

            if (_translatorOutputs.ResourcesForHtml.Count > 0)
            {
                outputForHtml = outputForHtml.Concat(_translatorOutputs.ResourcesForHtml);
            }

            var alreadyOutputedToMin = new HashSet<string>();

            foreach (var output in outputForHtml.Where(o => o.LoadInHtml))
            {
                if (output.OutputType == TranslatorOutputType.JavaScript && indexScript >= 0)
                {
                    if (output.IsMinified)
                    {
                        var path = output.GetOutputPath(outputPath, true);
                        if (alreadyOutputedToMin.Add(path))
                        {
                            jsMinBuffer.Append(Emitter.NEW_LINE);
                            jsMinBuffer.Append(indentScript);
                            jsMinBuffer.Append(string.Format(scriptTemplate, RemapToCDN(path)));
                        }
                    }
                    else
                    {
                        jsBuffer.Append(Emitter.NEW_LINE);
                        jsBuffer.Append(indentScript);
                        jsBuffer.Append(string.Format(scriptTemplate, RemapToCDN(output.GetOutputPath(outputPath, true))));

                        if (!output.IsEmpty && output.MinifiedVersion is object && output.MinifiedVersion.OutputType == TranslatorOutputType.JavaScript)
                        {
                            var path = output.MinifiedVersion.GetOutputPath(outputPath, true);
                            if (alreadyOutputedToMin.Add(path))
                            {
                                jsMinBuffer.Append(Emitter.NEW_LINE);
                                jsMinBuffer.Append(indentScript);
                                jsMinBuffer.Append(string.Format(scriptTemplate, RemapToCDN(path)));
                            }
                        }
                    }
                } 
                else if (output.OutputType == TranslatorOutputType.StyleSheets && indexCss >= 0)
                {
                    cssBuffer.Append(Emitter.NEW_LINE);
                    cssBuffer.Append(indentCss);
                    cssBuffer.Append(string.Format(cssLinkTemplate, RemapToCDN(output.GetOutputPath(outputPath, true))));
                }
            }

            var tokens = new Dictionary<string, string>()
            {
                [HtmlTokens.TITLE]  = _title,
                [HtmlTokens.CSS]    = cssBuffer.ToString(),
                [HtmlTokens.SCRIPT] = jsBuffer.ToString(),
                [HtmlTokens.BODY] = _assemblyConfig.Html.Body ?? "",
                [HtmlTokens.HEAD] = _assemblyConfig.Html.Head ?? "",
                [HtmlTokens.META] = _assemblyConfig.Html.Meta ?? "",
            };

            string htmlName = null;
            string htmlMinName = null;

            if (jsBuffer.Length > 0 || cssBuffer.Length > 0)
            {
                htmlName = "index.html";
            }

            if (jsMinBuffer.Length > 0)
            {
                htmlMinName = htmlName is null ? "index.html" : "index.min.html";
            }

            //Adds an extra logic here to only keep "one" index.html in the final output folder, depending on the type of build requested
            //This is useful when we want to generate both normal and minified formatings, but want to "consume" only one of them as the final output
            
            if (!string.IsNullOrEmpty(_buildConfig))
            {
                if(string.Equals(_buildConfig, "Release", StringComparison.InvariantCultureIgnoreCase))
                {
                    if(htmlMinName is object)
                    {
                        htmlName = null;
                        htmlMinName = "index.html";
                    }
                }
                else if (string.Equals(_buildConfig, "Debug", StringComparison.InvariantCultureIgnoreCase))
                {
                    if(htmlMinName is object && htmlName is object)
                    {
                        htmlMinName = null;
                    }
                }
            }

            var configHelper = new ConfigHelper();

            if (htmlName != null)
            {
                var html = configHelper.ApplyTokens(tokens, htmlTemplate);

                htmlName = Path.Combine(outputPath, htmlName);
                File.WriteAllText(htmlName, html, Translator.OutputEncoding);
            }

            if (htmlMinName != null)
            {
                tokens[HtmlTokens.SCRIPT] = jsMinBuffer.ToString();
                var html = configHelper.ApplyTokens(tokens, htmlTemplate);

                htmlMinName = Path.Combine(outputPath, htmlMinName);
                File.WriteAllText(htmlMinName, html, Translator.OutputEncoding);
            }

            Logger.LogTrace("GenerateHtml done");
        }

        private string GetIndent(string input, int index)
        {
            if (index <= 0 || input == null || index >= input.Length)
            {
                return "";
            }

            var indent = 0;

            while (index-- > 0)
            {
                if (input[index] != ' ')
                {
                    break;
                }

                indent++;
            }

            return new string(' ', indent);
        }

        private string ReadEmbeddedResource(string name)
        {
            var assembly = System.Reflection. Assembly.GetExecutingAssembly();

            using (Stream stream = assembly.GetManifestResourceStream(name))
            {
                using (StreamReader reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
        }
    }
}