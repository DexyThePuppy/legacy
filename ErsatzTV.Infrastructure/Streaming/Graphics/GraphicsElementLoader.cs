using System.IO.Abstractions;
using System.Text;
using System.Text.RegularExpressions;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Graphics;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Streaming;
using ErsatzTV.Core.Metadata;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Scriban;
using Scriban.Runtime;
using Scriban.Syntax;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ErsatzTV.Infrastructure.Streaming.Graphics;

public partial class GraphicsElementLoader(
    TemplateFunctions templateFunctions,
    IFileSystem fileSystem,
    ITemplateDataRepository templateDataRepository,
    ILogger<GraphicsElementLoader> logger)
    : IGraphicsElementLoader
{
    public async Task<GraphicsEngineContext> LoadAll(
        GraphicsEngineContext context,
        List<PlayoutItemGraphicsElement> elements,
        CancellationToken cancellationToken)
    {
        try
        {
            // get max epg entries
            int epgEntries = await GetMaxEpgEntries(elements);

            // init template element variables once
            Dictionary<string, object> templateVariables =
                await InitTemplateVariables(context, epgEntries, cancellationToken);

            // subtitles are in separate files, so they need template variables for later processing
            context = context with { TemplateVariables = templateVariables };

            // fully process references (using template variables)
            foreach (PlayoutItemGraphicsElement reference in elements)
            {
                switch (reference.GraphicsElement.Kind)
                {
                    case GraphicsElementKind.Text:
                    {
                        Option<TextGraphicsElement> maybeElement = await LoadText(
                            reference.GraphicsElement.Path,
                            templateVariables);
                        if (maybeElement.IsNone)
                        {
                            logger.LogWarning(
                                "Failed to load text graphics element from file {Path}; ignoring",
                                reference.GraphicsElement.Path);
                        }

                        foreach (TextGraphicsElement element in maybeElement)
                        {
                            context.Elements.Add(new TextElementDataContext(element));
                        }

                        break;
                    }
                    case GraphicsElementKind.Image:
                    {
                        Option<ImageGraphicsElement> maybeElement = await LoadImage(
                            reference.GraphicsElement.Path,
                            templateVariables);
                        if (maybeElement.IsNone)
                        {
                            logger.LogWarning(
                                "Failed to load image graphics element from file {Path}; ignoring",
                                reference.GraphicsElement.Path);
                        }

                        foreach (ImageGraphicsElement element in maybeElement)
                        {
                            context.Elements.Add(new ImageElementDataContext(element));
                        }

                        break;
                    }
                    case GraphicsElementKind.Motion:
                    {
                        Option<MotionGraphicsElement> maybeElement = await LoadMotion(
                            reference.GraphicsElement.Path,
                            templateVariables);
                        if (maybeElement.IsNone)
                        {
                            logger.LogWarning(
                                "Failed to load motion graphics element from file {Path}; ignoring",
                                reference.GraphicsElement.Path);
                        }

                        foreach (MotionGraphicsElement element in maybeElement)
                        {
                            context.Elements.Add(new MotionElementDataContext(element));
                        }

                        break;
                    }
                    case GraphicsElementKind.Subtitle:
                    {
                        Option<SubtitleGraphicsElement> maybeElement = await LoadSubtitle(
                            reference.GraphicsElement.Path,
                            templateVariables);
                        if (maybeElement.IsNone)
                        {
                            logger.LogWarning(
                                "Failed to load subtitle graphics element from file {Path}; ignoring",
                                reference.GraphicsElement.Path);
                        }

                        foreach (SubtitleGraphicsElement element in maybeElement)
                        {
                            var variables = new Dictionary<string, string>();
                            if (!string.IsNullOrWhiteSpace(reference.Variables))
                            {
                                variables = JsonConvert.DeserializeObject<Dictionary<string, string>>(
                                    reference.Variables);
                            }

                            context.Elements.Add(new SubtitleElementDataContext(element, variables));
                        }

                        break;
                    }
                    case GraphicsElementKind.Script:
                    {
                        Option<ScriptGraphicsElement> maybeElement = await LoadScript(
                            reference.GraphicsElement.Path,
                            templateVariables);
                        if (maybeElement.IsNone)
                        {
                            logger.LogWarning(
                                "Failed to load script graphics element from file {Path}; ignoring",
                                reference.GraphicsElement.Path);
                        }

                        foreach (ScriptGraphicsElement element in maybeElement)
                        {
                            context.Elements.Add(new ScriptElementDataContext(element));
                        }

                        break;
                    }
                    case GraphicsElementKind.Html:
                    {
                        Option<HtmlGraphicsElement> maybeElement = await LoadHtml(
                            reference.GraphicsElement.Path,
                            templateVariables);
                        if (maybeElement.IsNone)
                        {
                            logger.LogWarning(
                                "Failed to load HTML graphics element from file {Path}; ignoring",
                                reference.GraphicsElement.Path);
                        }

                        foreach (HtmlGraphicsElement element in maybeElement)
                        {
                            context.Elements.Add(new HtmlElementDataContext(element));
                        }

                        break;
                    }
                    default:
                        logger.LogInformation(
                            "Ignoring unsupported graphics element kind {Kind}",
                            nameof(reference.GraphicsElement.Kind));
                        break;
                }
            }

            return context;
        }
        catch (OperationCanceledException)
        {
            // do nothing
        }

        return null;
    }

    public async Task<Option<string>> TryLoadName(string fileName, CancellationToken cancellationToken)
    {
        try
        {
            string yaml = await fileSystem.File.ReadAllTextAsync(fileName, cancellationToken);
            var template = Template.Parse(yaml);

            var builder = new StringBuilder();
            var scriptPage = template.Page;

            if (scriptPage.Body != null)
            {
                foreach (var statement in scriptPage.Body.Statements)
                {
                    if (statement is ScriptRawStatement rawStatement)
                    {
                        builder.Append(rawStatement.Text);
                    }
                }
            }

            Option<BaseGraphicsElement> maybeElement = FromYamlIgnoreUnmatched<BaseGraphicsElement>(builder.ToString());
            foreach (BaseGraphicsElement element in maybeElement.Where(e => !string.IsNullOrWhiteSpace(e.Name)))
            {
                return element.Name;
            }
        }
        catch (Exception)
        {
            // do nothing
        }

        return Option<string>.None;
    }

    private async Task<int> GetMaxEpgEntries(List<PlayoutItemGraphicsElement> elements)
    {
        var epgEntries = 0;

        IEnumerable<PlayoutItemGraphicsElement> elementsWithEpg = elements.Where(e =>
            e.GraphicsElement.Kind is GraphicsElementKind.Text or GraphicsElementKind.Subtitle
                or GraphicsElementKind.Motion or GraphicsElementKind.Script or GraphicsElementKind.Image
                or GraphicsElementKind.Html);

        foreach (var reference in elementsWithEpg)
        {
            try
            {
                foreach (string line in await fileSystem.File.ReadAllLinesAsync(reference.GraphicsElement.Path))
                {
                    Match match = EpgEntriesRegex().Match(line);
                    if (!match.Success || !int.TryParse(match.Groups[1].Value, out int value))
                    {
                        continue;
                    }

                    epgEntries = Math.Max(epgEntries, value);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to read graphics element at {Path} for EPG entries",
                    reference.GraphicsElement.Path);
            }
        }

        return epgEntries;
    }

    private Task<Option<ImageGraphicsElement>> LoadImage(string fileName, Dictionary<string, object> variables) =>
        GetTemplatedYaml(fileName, variables).BindT(FromYaml<ImageGraphicsElement>);

    private Task<Option<TextGraphicsElement>> LoadText(string fileName, Dictionary<string, object> variables) =>
        GetTemplatedYaml(fileName, variables).BindT(FromYaml<TextGraphicsElement>);

    private Task<Option<MotionGraphicsElement>> LoadMotion(string fileName, Dictionary<string, object> variables) =>
        GetTemplatedYaml(fileName, variables).BindT(FromYaml<MotionGraphicsElement>);

    private Task<Option<SubtitleGraphicsElement>> LoadSubtitle(string fileName, Dictionary<string, object> variables) =>
        GetTemplatedYaml(fileName, variables).BindT(FromYaml<SubtitleGraphicsElement>);

    private Task<Option<ScriptGraphicsElement>> LoadScript(string fileName, Dictionary<string, object> variables) =>
        GetTemplatedYaml(fileName, variables).BindT(FromYaml<ScriptGraphicsElement>);

    private Task<Option<HtmlGraphicsElement>> LoadHtml(string fileName, Dictionary<string, object> variables) =>
        GetTemplatedYaml(fileName, variables).BindT(FromYaml<HtmlGraphicsElement>);

    private async Task<Dictionary<string, object>> InitTemplateVariables(
        GraphicsEngineContext context,
        int epgEntries,
        CancellationToken cancellationToken)
    {
        // common variables
        var result = new Dictionary<string, object>
        {
            [FFmpegProfileTemplateDataKey.Resolution] = context.FrameSize,
            [FFmpegProfileTemplateDataKey.ScaledResolution] = context.SquarePixelFrameSize,
            [FFmpegProfileTemplateDataKey.RFrameRate] = context.FrameRate.RFrameRate,
            [FFmpegProfileTemplateDataKey.FrameRate] = context.FrameRate.ParsedFrameRate,
            [ChannelTemplateDataKey.ChannelStartTime] = context.ChannelStartTime,
            [ChannelTemplateDataKey.Number] = context.ChannelNumber,
            [MediaItemTemplateDataKey.StreamSeek] = context.Seek,
            [MediaItemTemplateDataKey.Start] = context.ContentStartTime,
            [MediaItemTemplateDataKey.Stop] = context.ContentStartTime + context.Duration,
            [MediaItemTemplateDataKey.DurationSeconds] = context.ContentTotalDuration.TotalSeconds,
            [MediaItemTemplateDataKey.StreamSeekSeconds] = context.Seek.TotalSeconds,
            [MediaItemTemplateDataKey.RemainingSeconds] =
                Math.Max(0, (context.ContentTotalDuration - context.Seek).TotalSeconds)
        };

        // media item variables
        Option<Dictionary<string, object>> maybeTemplateData =
            await templateDataRepository.GetMediaItemTemplateData(context.MediaItem, cancellationToken);
        foreach (Dictionary<string, object> templateData in maybeTemplateData)
        {
            foreach (KeyValuePair<string, object> variable in templateData)
            {
                result[variable.Key] = variable.Value;
            }
        }

        // epg variables (always fetch at least two entries so Next_* variables are available)
        DateTimeOffset startTime = context.ContentStartTime + context.Seek;
        Option<Dictionary<string, object>> maybeEpgData =
            await templateDataRepository.GetEpgTemplateData(context.ChannelNumber, startTime, Math.Max(epgEntries, 2));
        foreach (Dictionary<string, object> templateData in maybeEpgData)
        {
            foreach (KeyValuePair<string, object> variable in templateData)
            {
                result[variable.Key] = variable.Value;
            }
        }

        AddNextEpgEntryVariables(result, startTime);

        // trim epg entries back to the requested count so existing templates render unchanged
        if (result.TryGetValue(EpgTemplateDataKey.Epg, out object epg) &&
            epg is System.Collections.IEnumerable epgEnumerable)
        {
            result[EpgTemplateDataKey.Epg] = epgEnumerable.Cast<object>().Take(epgEntries).ToList();
        }

        return result;
    }

    private static void AddNextEpgEntryVariables(Dictionary<string, object> result, DateTimeOffset startTime)
    {
        if (!result.TryGetValue(EpgTemplateDataKey.Epg, out object epgValue) ||
            epgValue is not System.Collections.IEnumerable enumerable)
        {
            return;
        }

        var entries = enumerable.Cast<object>().ToList();
        if (entries.Count < 2)
        {
            return;
        }

        switch (entries[1])
        {
            case EpgProgrammeTemplateData typed:
                result[EpgTemplateDataKey.NextTitle] = typed.Title;
                result[EpgTemplateDataKey.NextSubTitle] = typed.SubTitle;
                result[EpgTemplateDataKey.NextDescription] = typed.Description;
                result[EpgTemplateDataKey.NextStart] = typed.Start;
                result[EpgTemplateDataKey.NextStop] = typed.Stop;
                result[EpgTemplateDataKey.NextStartsInSeconds] =
                    Math.Max(0, (typed.Start - startTime).TotalSeconds);
                break;
            case Dictionary<string, object> dict:
                result[EpgTemplateDataKey.NextTitle] = dict.GetValueOrDefault("Title");
                result[EpgTemplateDataKey.NextSubTitle] = dict.GetValueOrDefault("SubTitle");
                result[EpgTemplateDataKey.NextDescription] = dict.GetValueOrDefault("Description");
                result[EpgTemplateDataKey.NextStart] = dict.GetValueOrDefault("Start");
                result[EpgTemplateDataKey.NextStop] = dict.GetValueOrDefault("Stop");
                if (dict.GetValueOrDefault("Start") is DateTimeOffset nextStart)
                {
                    result[EpgTemplateDataKey.NextStartsInSeconds] =
                        Math.Max(0, (nextStart - startTime).TotalSeconds);
                }

                break;
        }
    }

    private async Task<Option<TemplatedYaml>> GetTemplatedYaml(string fileName, Dictionary<string, object> variables)
    {
        string yaml = await fileSystem.File.ReadAllTextAsync(fileName);
        try
        {
            var scriptObject = new ScriptObject();
            scriptObject.Import(variables, renamer: member => member.Name);
            scriptObject.Import("convert_timezone", templateFunctions.ConvertTimeZone);
            scriptObject.Import("format_datetime", templateFunctions.FormatDateTime);
            scriptObject.Import("get_directory_name", (string path) => Path.GetDirectoryName(path));
            scriptObject.Import("get_filename_without_extension", (string path) => Path.GetFileNameWithoutExtension(path));

            var context = new TemplateContext { MemberRenamer = member => member.Name };
            context.PushGlobal(scriptObject);
            return new TemplatedYaml(
                fileSystem.Path.GetFileName(fileName),
                await Template.Parse(yaml).RenderAsync(context));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to render graphics element YAML definition as scriban template");
            return Option<TemplatedYaml>.None;
        }
    }

    private Option<T> FromYaml<T>(TemplatedYaml yaml) where T : BaseGraphicsElement
    {
        try
        {
            // TODO: validate schema
            // if (await yamlScheduleValidator.ValidateSchedule(yaml, isImport) == false)
            // {
            //     return Option<YamlPlayoutDefinition>.None;
            // }

            IDeserializer deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build();

            var result = deserializer.Deserialize<T>(yaml.Yaml);
            result.SourceFileName = yaml.SourceFileName;
            return result;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load graphics element YAML definition");
            return Option<T>.None;
        }
    }

    private static Option<T> FromYamlIgnoreUnmatched<T>(string yaml)
    {
        try
        {
            IDeserializer deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            return deserializer.Deserialize<T>(yaml);
        }
        catch (Exception)
        {
            return Option<T>.None;
        }
    }

    [GeneratedRegex(@"epg_entries:\s*(\d+)")]
    private static partial Regex EpgEntriesRegex();

    private record TemplatedYaml(string SourceFileName, string Yaml);
}
