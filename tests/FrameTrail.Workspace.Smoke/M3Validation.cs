using System.Security.Cryptography;
using System.Text.Json;
using FrameTrail.Domain;
using FrameTrail.Interop;
using FrameTrail.Workspace;

internal static class M3Validation
{
    internal static async Task<int> RunAsync(string fixture, string output)
    {
        fixture = Path.GetFullPath(fixture); output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var checks = new List<string>();
        try
        {
            var path = Path.Combine(output, "M3.frametrailproject");
            await using (var workspace = ProjectWorkspace.Create(path, "m3-fixture"))
            {
                foreach (var name in new[] { "screen.mp4", "camera.mp4" }) File.Copy(Path.Combine(fixture, "media", name), workspace.Resolve("media/" + name));
                var screen = new MediaAsset { Id = "screen", Kind = MediaKind.Screen, Path = "media/screen.mp4", StartOffset = MediaTime.FromTicks(0), Duration = MediaTime.FromTicks(30_000_000), Width = 640, Height = 360 };
                var camera = screen with { Id = "camera", Kind = MediaKind.Camera, Path = "media/camera.mp4", Width = 320, Height = 240, Duration = MediaTime.FromTicks(20_000_000), StartOffset = MediaTime.FromTicks(5_000_000) };
                await File.WriteAllLinesAsync(workspace.Resolve("events/pointer.jsonl"), Enumerable.Range(0, 61).Select(i => JsonSerializer.Serialize(new { type = "pointer", ticks = i * 500_000L, timescale = 10_000_000, x = 0.75 + i / 600.0, y = 0.38, visible = true, inside = true, shape = 0, kind = i == 20 ? 1 : i == 21 ? 2 : 0 })));
                var pointer = screen with { Id = "pointer", Kind = MediaKind.PointerEvents, Path = "events/pointer.jsonl", Width = 0, Height = 0 };
                var initial = new ProjectSnapshot { ProjectId = "m3-fixture", Revision = 1, SourceDuration = MediaTime.FromTicks(30_000_000), Media = new[] { screen, camera, pointer }, Segments = new[] { new RecordingSegment { Id = "clip", SourceDuration = MediaTime.FromTicks(30_000_000), SourceStart = MediaTime.FromTicks(0), Rate = new() } }, Canvas = new() { Width = 640, Height = 360, Padding = 0.08, Camera = new() { Enabled = true } }, Export = new() { Width = 640, Height = 360 }, Editor = new() };
                await workspace.SaveAsync(initial, workspace.SessionEpoch); var editor = new EditorSession(workspace);
                var image = await editor.ImportVisualAsync(Path.Combine(fixture, "assets", "sticker.png"), false);
                var background = await editor.ImportVisualAsync(Path.Combine(fixture, "assets", "background.png"), false);
                var video = await editor.ImportVisualAsync(Path.Combine(fixture, "assets", "background.mp4"), true);
                await editor.AutoZoomAsync(); var generated = editor.Snapshot.Effects.Single();
                await editor.AutoZoomAsync(); if (editor.Snapshot.Effects.Count != 1 || editor.Snapshot.Effects[0].Parameters.GetRawText() != generated.Parameters.GetRawText()) throw new IOException("Pointer JSONL planning was not idempotent");
                await editor.UpsertEffectAsync(generated); await editor.AutoZoomAsync();
                if (editor.Snapshot.Effects.Single().Parameters.GetProperty("origin").GetString() != "edited") throw new IOException("Planner overwrote authored effect");
                await editor.DeleteEffectAsync(generated.Id); checks.Add("Recorded pointer JSONL plans idempotently and preserves authored zoom edits");
                SourceEffect Effect(string kind, object parameters, string? id = null) => new() { Id = id ?? "effect-" + kind, Kind = kind, SourceStart = MediaTime.FromTicks(0), SourceDuration = MediaTime.FromTicks(30_000_000), Parameters = JsonSerializer.SerializeToElement(parameters) };
                var cases = new Dictionary<string, SourceEffect[]> {
                    ["neutral"] = Array.Empty<SourceEffect>(),
                    ["zoom"] = new[] { Effect("zoom", new { scale = 2.0, focusX = 0.7, focusY = 0.4, enter = 0.5, exit = 0.5, easing = "spring" }) },
                    ["tilt"] = new[] { Effect("tilt", new { angleX = 18, angleY = -24, enter = 0.3, exit = 0.3 }) },
                    ["solid"] = new[] { Effect("mask", new { x = 0.25, y = 0.25, width = 0.5, height = 0.5, mode = "solid" }) },
                    ["pixelate"] = new[] { Effect("mask", new { x = 0.25, y = 0.25, width = 0.5, height = 0.5, mode = "pixelate", strength = 28 }) },
                    ["blur"] = new[] { Effect("mask", new { x = 0.25, y = 0.25, width = 0.5, height = 0.5, mode = "blur", strength = 18 }) },
                    ["focus"] = new[] { Effect("focus", new { x = 0.25, y = 0.25, width = 0.5, height = 0.5, strength = 18 }) },
                    ["sticker"] = new[] { Effect("sticker", new { path = image, x = 0.2, y = 0.2, width = 0.25, height = 0.25, rotation = 12, opacity = 0.8 }) },
                    ["text"] = new[] { Effect("sticker", new { text = "重点", x = 0.2, y = 0.2, width = 0.3, height = 0.2, attachment = "canvas" }) },
                    ["intro"] = new[] { Effect("intro", new { style = "rise", enter = 2.0, exit = 0.0 }) },
                    ["camera"] = new[] { Effect("intro", new { target = "camera", style = "scale", enter = 2.0, exit = 0.4 }) }
                };
                cases["combined"] = new[] { cases["zoom"][0], cases["tilt"][0], cases["pixelate"][0], cases["sticker"][0], cases["camera"][0] };
                cases["combined-no-cursor"] = cases["combined"];
                cases["canvas-mask"] = new[] { Effect("mask", new { x = 0.68, y = 0.58, width = 0.32, height = 0.42, mode = "solid", attachment = "canvas" }) };
                cases["privacy-overlap"] = new[] { cases["solid"][0], cases["focus"][0], Effect("mask", new { x = 0.2, y = 0.2, width = 0.6, height = 0.6, mode = "blur" }, "blur-overlap") };
                foreach (var style in new[] { "fade", "scale", "slide" }) cases["intro-" + style] = new[] { Effect("intro", new { style, enter = 2.0, exit = 0.0 }) };
                foreach (var (name, effects) in cases)
                {
                    var snapshot = initial with { Effects = effects, Editor = initial.Editor! with { CameraShape = "circle", FrameStyle = name.StartsWith("combined") ? "browser" : "none", CursorScale = name == "combined-no-cursor" ? 0 : 1 } };
                    using var media = new MediaSession(snapshot, path); media.Render(10_000_000, png: Path.Combine(output, name + "-preview.png")); media.Export(Path.Combine(output, name + ".mp4"));
                    checks.Add(name + ": preview and finalized export produced");
                }
                foreach (var frame in new[] { "window", "browser", "device" })
                {
                    using var media = new MediaSession(initial with { Editor = initial.Editor! with { FrameStyle = frame } }, path);
                    media.Render(10_000_000, png: Path.Combine(output, "frame-" + frame + "-preview.png")); media.Export(Path.Combine(output, "frame-" + frame + ".mp4")); checks.Add("frame-" + frame + ": preview/export");
                }
                foreach (var shape in new[] { "rounded", "rectangle", "circle" })
                {
                    using var media = new MediaSession(initial with { Editor = initial.Editor! with { CameraShape = shape } }, path);
                    media.Render(10_000_000, png: Path.Combine(output, "camera-" + shape + "-preview.png")); media.Export(Path.Combine(output, "camera-" + shape + ".mp4")); checks.Add("camera-" + shape + ": preview/export");
                }
                using (var media = new MediaSession(initial with { Effects = cases["combined"], Export = initial.Export with { Fps = new() { Numerator = 60, Denominator = 1 } } }, path))
                { media.Render(10_000_000, png: Path.Combine(output, "combined-60-preview.png")); media.Export(Path.Combine(output, "combined-60.mp4")); checks.Add("Combined effects at 60 FPS: preview/export"); }
                foreach (var (kind, asset) in new[] { ("gradient", ""), ("image", background), ("video", video) })
                {
                    using var media = new MediaSession(initial with { Editor = initial.Editor! with { Backdrop = new() { Kind = kind, Path = asset } } }, path);
                    media.Render(25_000_000, png: Path.Combine(output, "background-" + kind + "-preview.png")); media.Export(Path.Combine(output, "background-" + kind + ".mp4")); checks.Add("background-" + kind + ": imported asset and loop render");
                }
                var combined = initial with { Effects = cases["combined"] };
                using (var live = new MediaSession(initial, path)) {
                    live.Render(10_000_000, png: Path.Combine(output, "live-base.png"));
                    var chain = live.AcquireSwapChain();
                    try {
                        for (ulong g = 1; g <= 12; g++) { live.UpdateEffects(combined with { Revision = (long)g + 1 }, g); live.Render(15_000_000, g); }
                        live.Render(15_000_000, 12, Path.Combine(output, "live-updated.png"));
                        var sameChain = live.AcquireSwapChain();
                        try { if (chain != sameChain) throw new IOException("Live edit replaced the swap chain"); } finally { System.Runtime.InteropServices.Marshal.Release(sameChain); }
                        live.SetGeneration(14);
                        try { live.UpdateEffects(initial, 13); throw new IOException("Stale effect update accepted"); } catch (OperationCanceledException) { }
                        try { live.UpdateEffects(initial with { Effects = new[] { Effect("zoom", new { scale = 99 }) } }, 14); throw new IOException("Invalid live effect accepted"); } catch (System.Runtime.InteropServices.COMException) { }
                        live.Render(15_000_000, 14, Path.Combine(output, "live-after-rejection.png"));
                        if (!SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(output, "live-updated.png"))).SequenceEqual(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(output, "live-after-rejection.png"))))) throw new IOException("Rejected live update changed pixels");
                        live.UpdateEffects(initial, 15); live.Render(10_000_000, 15, Path.Combine(output, "live-reset.png"));
                        if (!SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(output, "live-base.png"))).SequenceEqual(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(output, "live-reset.png"))))) throw new IOException("Reverted live update changed neutral pixels");
                    } finally { System.Runtime.InteropServices.Marshal.Release(chain); }
                }
                checks.Add("Live updates retain the swap chain, reject stale/invalid drafts atomically, and restore pixels on undo");
                using (var slow = new MediaSession(combined, path)) { for (long t = 0; t < 15_000_000; t += 2_000_000) slow.Render(t); slow.Render(15_000_000, png: Path.Combine(output, "slow.png")); }
                using (var fast = new MediaSession(combined, path)) { for (long t = 0; t < 15_000_000; t += 166_667) fast.Render(t); fast.Render(15_000_000, png: Path.Combine(output, "fast.png")); }
                if (!SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(output, "live-updated.png"))).SequenceEqual(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(output, "fast.png"))))) throw new IOException("Live scene differs from fresh evaluated scene");
                if (!SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(output, "slow.png"))).SequenceEqual(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(output, "fast.png"))))) throw new IOException("Preview rate changed the authored trajectory.");
                checks.Add("Low-rate, high-rate and direct seek render identical motion pixels");
                await editor.UpsertEffectAsync(cases["zoom"][0]); await editor.PasteEffectAsync(cases["zoom"][0], 10_000_000); var edited = ProjectJson.Serialize(editor.Snapshot with { Revision = 0 });
                await editor.UndoAsync(); await editor.RedoAsync(); if (ProjectJson.Serialize(editor.Snapshot with { Revision = 0 }) != edited) throw new IOException("Effect paste undo/redo changed content");
                await editor.ApplyPresetAsync("studio"); await editor.SaveVisualPresetAsync(); var preset = ProjectJson.Serialize(editor.Snapshot with { Revision = 0 }); await editor.ApplyPresetAsync("clean"); await editor.LoadVisualPresetAsync();
                if (ProjectJson.Serialize(editor.Snapshot with { Revision = 0 }) != preset) throw new IOException("Saved preset changed content");
                await editor.ExecuteAsync(new { op = "settings", editor = editor.Snapshot.Editor! with { SystemGain = 0.4, MicrophoneMuted = true } }); await editor.LoadVisualPresetAsync();
                if (editor.Snapshot.Editor?.SystemGain != 0.4 || editor.Snapshot.Editor.MicrophoneMuted != true) throw new IOException("Visual preset overwrote audio");
                var revision = editor.Snapshot.Revision;
                try { await editor.UpsertEffectAsync(Effect("mask", new { width = 9 })); throw new IOException("Invalid effect accepted"); } catch (EngineException) { }
                if (editor.Snapshot.Revision != revision) throw new IOException("Rejected effect changed revision");
                await editor.UpsertEffectAsync(cases["sticker"][0]); await editor.ExecuteAsync(new { op = "settings", editor = editor.Snapshot.Editor! with { Backdrop = new() { Kind = "image", Path = background } } });
                using (var media = new MediaSession(editor.Snapshot, path)) media.Render(10_000_000, png: Path.Combine(output, "portable-original.png"));
                await editor.FlushAsync(); checks.Add("Effect paste, undo/redo and saved project preset roundtrip");
                await editor.CopySourcesAsync(Path.Combine(output, "Portable.frametrailproject"), CancellationToken.None);
            }
            await using (var portable = await ProjectWorkspace.OpenAsync(Path.Combine(output, "Portable.frametrailproject"))) { using var media = new MediaSession(portable.Snapshot!, portable.DirectoryPath); media.Render(10_000_000, png: Path.Combine(output, "portable.png")); }
            checks.Add("Copied project reopens with project-relative visual assets");
            await File.WriteAllTextAsync(Path.Combine(output, "m3-validation.json"), JsonSerializer.Serialize(new { success = true, checks, timestampUtc = DateTimeOffset.UtcNow }, new JsonSerializerOptions { WriteIndented = true })); return 0;
        }
        catch (Exception error) { await File.WriteAllTextAsync(Path.Combine(output, "m3-validation.json"), JsonSerializer.Serialize(new { success = false, checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true })); Console.Error.WriteLine(error); return 1; }
    }
}
