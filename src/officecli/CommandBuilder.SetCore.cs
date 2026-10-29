// Copyright 2026 OfficeCLI (https://OfficeCLI.AI)
// SPDX-License-Identifier: Apache-2.0

using OfficeCli.Core;

namespace OfficeCli;

/// <summary>What RunSet applied and how the front-end should finish: exit code
/// (0 applied, 2 applied with unsupported props / unrecognised LaTeX), the
/// unsupported list and the suggestion scope for any follow-up paths.</summary>
internal sealed record SetOutcome(int ExitCode, List<string> Unsupported, string? SuggestionScope);

static partial class CommandBuilder
{
    /// <summary>
    /// The `set` command proper for an already-open handler: apply the props
    /// (with auto-correction), build the receipt and the warnings, print them
    /// (text or envelope). One implementation shared by the CLI front-end and
    /// the resident so the two cannot drift. The caller handles what is
    /// specific to it: the front-end's deferred stdout, watch notifications
    /// and extra selected paths; the resident's exit-code plumbing.
    /// </summary>
    internal static SetOutcome RunSet(IDocumentHandler handler, string filePath, string path,
        Dictionary<string, string> properties, bool json, bool isSelectorSet)
    {
        // Scope the unsupported-prop fuzzy-suggestion pool by handler type
        // so e.g. Excel pivot errors don't suggest PPTX-only keys like
        // 'rotation' for an unknown 'location' prop (R2-4). Kept local: the
        // shared core computes its own copy, but the CLI warning / extra-path
        // decoration below also needs it.
        string? suggestionScope = handler switch
        {
            OfficeCli.Handlers.ExcelHandler => "excel",
            OfficeCli.Handlers.WordHandler => "word",
            OfficeCli.Handlers.PowerPointHandler => "pptx",
            _ => null,
        };

        // Shared core: apply + prop-autocorrect + categorise (one copy for
        // CLI / batch / MCP / resident; see ApplySetWithCorrection). The rich
        // CLI envelope below — find-count, position overlap, --json warnings,
        // exit codes — stays here.
        // CONSISTENCY(applied-echo): pre/post Format snapshots feed the
        // " (applied: ...)" normalization echo; mirrored in
        // ResidentServer.ExecuteSet.
        var beforeSnap = TryGetFormatSnapshot(handler, path);
        var (applied, stillUnsupported, autoCorrected) = ApplySetWithCorrection(handler, path, properties);

        // Get find match count if applicable.
        // find-match count: surfaced on both paths through this one implementation.
        // The resident path is hit whenever a resident process is open
        // (which `create` does by default), so both sites must surface
        // findMatchCount + zero_matches warning identically.
        int? findMatchCount = null;
        if (properties.ContainsKey("find"))
        {
            findMatchCount = handler switch
            {
                OfficeCli.Handlers.WordHandler wh => wh.LastFindMatchCount,
                OfficeCli.Handlers.PowerPointHandler ph => ph.LastFindMatchCount,
                OfficeCli.Handlers.ExcelHandler eh => eh.LastFindMatchCount,
                _ => null
            };
        }

        // CONSISTENCY(selector-set): echo how many elements a multi-match
        // selector set touched so a Sheet1!row[工资>5000]-style change shows
        // its scope. Single-target paths (count 1) stay quiet.
        // A content-filter path (/Sheet1/cell[type=String]) is a selector too:
        // report the count whenever the handler expanded one to several targets.
        int? selectorCount = handler switch
        {
            OfficeCli.Handlers.WordHandler wh => wh.LastSelectorSetCount,
            OfficeCli.Handlers.PowerPointHandler ph => ph.LastSelectorSetCount,
            OfficeCli.Handlers.ExcelHandler eh => eh.LastSelectorSetCount,
            _ => null
        };

        // R4-bt-1: an equation mode switch MOVES the element (oMathPara ⇄
        // oMath), changing its canonical path. Report the NEW resolvable
        // path so the "Updated …" line points at a path that still resolves.
        var reportPath = (handler as OfficeCli.Handlers.WordHandler)?.LastSetNewPath ?? path;
        var appliedSuffix = BuildAppliedSuffix(applied,
            beforeSnap, TryGetFormatSnapshot(handler, reportPath));
        var message = applied.Count > 0
            ? $"Updated {reportPath}: {string.Join(", ", applied.Select(kv => $"{kv.Key}={kv.Value}"))}"
              + appliedSuffix
              + (findMatchCount.HasValue ? $" ({findMatchCount.Value} matched)" : "")
              + (selectorCount > 1 ? $" ({selectorCount} elements matched)" : "")
            : $"Error: No properties applied to {path}";

        // Check if position-related props were changed → show coordinates + overlap warning
        var positionChanged = applied.Any(kv => PositionKeys.Contains(kv.Key));
        string? setSpatialLine = null;
        var setOverlaps = new List<string>();
        if (positionChanged)
        {
            setSpatialLine = GetPptSpatialLine(handler, path);
            if (setSpatialLine != null) setOverlaps = CheckPositionOverlap(handler, path);
        }

        // Unrecognized LaTeX commands/environments from an equation Set
        // (formula=). Same UX as unsupported_property (warning + JSON
        // envelope + exit 2); the equation is still written (lenient
        // accept). CONSISTENCY: mirrors CommandBuilder.Add and
        // ResidentServer.ExecuteSet.
        var setUnrecognizedLatex = handler switch
        {
            OfficeCli.Handlers.WordHandler wlx => wlx.LastUnrecognizedLatex,
            OfficeCli.Handlers.PowerPointHandler plx => plx.LastUnrecognizedLatex,
            _ => null,
        };
        bool hasUnrecognizedLatex = setUnrecognizedLatex is { Count: > 0 };

        if (json)
        {
            var allWarnings = new List<OfficeCli.Core.CliWarning>();
            if (hasUnrecognizedLatex)
            {
                foreach (var tok in setUnrecognizedLatex!)
                    allWarnings.Add(new OfficeCli.Core.CliWarning
                    {
                        Message = $"unrecognized_latex_command: {tok}",
                        Code = "unrecognized_latex_command",
                        Suggestion = "Check the command spelling; see https://katex.org/docs/supported.html for supported syntax.",
                    });
            }
            if (findMatchCount is 0)
            {
                allWarnings.Add(new OfficeCli.Core.CliWarning
                {
                    Message = $"find pattern matched 0 occurrences at {path} — original text may have been edited or the path is wrong",
                    Code = "zero_matches",
                    Suggestion = "verify the path still resolves and the find text is current"
                });
            }
            foreach (var ac in autoCorrected)
            {
                allWarnings.Add(new OfficeCli.Core.CliWarning
                {
                    Message = $"Auto-corrected '{ac.Original}' to '{ac.Corrected}'",
                    Code = "auto_corrected",
                    Suggestion = ac.Corrected
                });
            }
            foreach (var p in stillUnsupported)
            {
                // An entry that already carries a handler-embedded hint
                // ("薪水 (no such column; available: …)") must not get a
                // generic did-you-mean stacked on top — the handler already
                // said what's valid. Mirrors CommandBuilder.FormatUnsupported.
                var suggestion = p.Contains('(') ? null : SuggestPropertyScoped(p, suggestionScope);
                allWarnings.Add(new OfficeCli.Core.CliWarning
                {
                    Message = suggestion != null ? $"Unsupported property: {p} (did you mean: {suggestion}?)" : $"Unsupported property: {p}",
                    Code = "unsupported_property",
                    Suggestion = suggestion
                });
            }
            if (setOverlaps.Count > 0)
            {
                allWarnings.Add(new OfficeCli.Core.CliWarning
                {
                    Message = $"Same position as {string.Join(", ", setOverlaps)}",
                    Code = "position_overlap",
                    Suggestion = "Use different x/y values to avoid overlap"
                });
            }
            var setOverflow = CheckTextOverflow(handler, path);
            if (setOverflow != null)
            {
                allWarnings.Add(new OfficeCli.Core.CliWarning
                {
                    Message = setOverflow,
                    Code = "text_overflow",
                    Suggestion = "Increase shape height/width, reduce font size, or shorten text"
                });
            }
            if (handler is OfficeCli.Handlers.WordHandler setWhWarn)
            {
                foreach (var w in setWhWarn.LastSetWarnings)
                    allWarnings.Add(new OfficeCli.Core.CliWarning { Message = w, Code = "advisory" });
            }
            var outputMsg = setSpatialLine != null ? $"{message}\n  {setSpatialLine}" : message;
            // applied==0 implies no key auto-corrected (corrections land in
            // applied), so stillUnsupported already equals the raw set, and
            // the old `|| unsupported.Count>0` term was redundant.
            bool allFailed = applied.Count == 0 && stillUnsupported.Count > 0;
            Console.WriteLine(allFailed
                ? OutputFormatter.WrapEnvelopeError(outputMsg, allWarnings.Count > 0 ? allWarnings : null)
                : OutputFormatter.WrapEnvelopeText(outputMsg, allWarnings.Count > 0 ? allWarnings : null, findMatchCount));
        }
        else
        {
            foreach (var ac in autoCorrected)
                Console.Error.WriteLine($"WARNING: Auto-corrected '{ac.Original}' to '{ac.Corrected}'");
            Console.WriteLine(message);
            if (findMatchCount is 0)
                Console.Error.WriteLine($"WARNING: find pattern matched 0 occurrences at {path}");
            if (setSpatialLine != null) Console.WriteLine($"  {setSpatialLine}");
            if (setOverlaps.Count > 0)
                Console.Error.WriteLine($"  WARNING: Same position as {string.Join(", ", setOverlaps)}");
            var setOverflowPlain = CheckTextOverflow(handler, path);
            if (setOverflowPlain != null)
                Console.Error.WriteLine($"  WARNING: {setOverflowPlain}");
            if (stillUnsupported.Count > 0)
                Console.Error.WriteLine(FormatUnsupported(stillUnsupported, suggestionScope));
            if (handler is OfficeCli.Handlers.WordHandler setWhWarnPlain)
            {
                foreach (var w in setWhWarnPlain.LastSetWarnings)
                    Console.Error.WriteLine($"  WARNING: {w}");
            }
            if (hasUnrecognizedLatex)
                foreach (var tok in setUnrecognizedLatex!)
                    Console.Error.WriteLine($"  WARNING: unrecognized_latex_command: {tok}");
        }

        var exitCode = stillUnsupported.Count > 0 || hasUnrecognizedLatex ? 2 : 0;
        return new SetOutcome(exitCode, stillUnsupported, suggestionScope);
    }
}
