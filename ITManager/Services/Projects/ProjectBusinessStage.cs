// File: Models/Projects/ProjectBusinessStage.cs
// Description: Kanoniczne etapy biznesowe projektu (Draft, Planning, Execution, Validation, Done, OnHold)
//              oraz mapowanie nazw statusów ze słownika DB do tych etapów.
// Version: 1.00
// Created: 2026-01-27
// Change history:
// - 1.00: Initial.

using System;

namespace ITManager.Models.Projects
{
    public enum ProjectBusinessStage
    {
        Draft = 1,
        Planning = 2,
        Execution = 3,
        Validation = 4,
        Done = 5,
        OnHold = 6
    }

    public static class ProjectBusinessStageResolver
    {
        public static ProjectBusinessStage Resolve(string? projectStatusName)
        {
            var s = (projectStatusName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(s))
                return ProjectBusinessStage.Draft;

            var key = s.ToLowerInvariant();

            // Canonical names
            if (key == "draft") return ProjectBusinessStage.Draft;
            if (key == "planning") return ProjectBusinessStage.Planning;
            if (key == "execution") return ProjectBusinessStage.Execution;
            if (key == "validation") return ProjectBusinessStage.Validation;
            if (key == "done") return ProjectBusinessStage.Done;
            if (key == "onhold" || key == "on hold") return ProjectBusinessStage.OnHold;

            // Common synonyms (PL/EN) to stay robust if DB lookup uses other labels.
            if (key.Contains("hold") || key.Contains("paused") || key.Contains("pause") || key.Contains("wstrzym"))
                return ProjectBusinessStage.OnHold;

            if (key.Contains("validate") || key.Contains("test") || key.Contains("accept") || key.Contains("akcept") || key.Contains("walid"))
                return ProjectBusinessStage.Validation;

            if (key.Contains("done") || key.Contains("closed") || key.Contains("complete") || key.Contains("zako") || key.Contains("zamkn"))
                return ProjectBusinessStage.Done;

            if (key.Contains("exec") || key.Contains("in progress") || key.Contains("progress") || key.Contains("realiz") || key.Contains("w trak"))
                return ProjectBusinessStage.Execution;

            if (key.Contains("plan") || key.Contains("ready") || key.Contains("do decyz") || key.Contains("zaplan"))
                return ProjectBusinessStage.Planning;

            return ProjectBusinessStage.Draft;
        }

        public static string ToDisplayName(ProjectBusinessStage stage)
        {
            return stage switch
            {
                ProjectBusinessStage.Draft => "Draft",
                ProjectBusinessStage.Planning => "Planning",
                ProjectBusinessStage.Execution => "Execution",
                ProjectBusinessStage.Validation => "Validation",
                ProjectBusinessStage.Done => "Done",
                ProjectBusinessStage.OnHold => "On hold",
                _ => "Draft"
            };
        }
    }
}
