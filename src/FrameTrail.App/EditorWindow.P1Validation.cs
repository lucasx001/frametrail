using FrameTrail.Domain;
using FrameTrail.Interop;
using Microsoft.UI.Xaml;
using System.Diagnostics;
using System.Text.Json;

namespace FrameTrail.App;

public sealed partial class EditorWindow
{
    private async Task RunP1ValidationAsync(string directory)
    {
        Directory.CreateDirectory(directory);var checks=new List<string>();bool success=false;string? failure=null;
        try {
            position=Math.Min(10_000_000,editor.Timeline.Duration/2);
            foreach(string kind in new[]{"mask","focus","sticker"}) {
                await PerformAsync(()=>AddVisualAsync(kind));
                var id=selectedEffect!;var effect=CurrentEffect!;
                var p=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(effect.Parameters.GetRawText())!;
                p["x"]=JsonSerializer.SerializeToElement(0.2);p["y"]=JsonSerializer.SerializeToElement(0.2);
                p["width"]=JsonSerializer.SerializeToElement(0.3);p["height"]=JsonSerializer.SerializeToElement(0.3);
                if(kind=="sticker")p["rotation"]=JsonSerializer.SerializeToElement(25);
                await PerformAsync(()=>editor.UpsertEffectAsync(effect with {Parameters=JsonSerializer.SerializeToElement(p)}));
                await preview.RenderAsync(position);QueueCanvasGeometry();await Task.Delay(120);
                var item=(await Task.Run(()=>preview.Session!.EditGeometry(position))).First(i=>i.Id==id);
                var basePlane=new CanvasPlane(item.Base);var center=basePlane.Map(0.35,0.35);
                string before=ProjectJson.Serialize(editor.Snapshot with {Revision=0});
                if(!canvasOverlay.BeginGesture(center.X,center.Y))throw new InvalidOperationException("Canvas hit failed: "+kind);
                var target=basePlane.Map(0.4,0.4);canvasOverlay.MoveGesture(target.X,target.Y);canvasOverlay.Finish(true);await canvasCommitWork;
                var moved=editor.Snapshot.Effects.First(e=>e.Id==id);if(Math.Abs(NumberValue(moved.Parameters,"x",0)-0.25)>0.00001)throw new InvalidOperationException("Canvas move differs from projected input.");
                await PerformAsync(editor.UndoAsync);if(ProjectJson.Serialize(editor.Snapshot with {Revision=0})!=before)throw new InvalidOperationException("Canvas drag needs more than one undo.");
                await PerformAsync(editor.RedoAsync);await preview.RenderAsync(position,true,Path.Combine(directory,kind+"-live.png"));
                using(var fresh=new MediaSession(editor.Snapshot,editor.DirectoryPath))await Task.Run(()=>{fresh.SetPreviewQuality(preview.Quality);fresh.Render(position,png:Path.Combine(directory,kind+"-fresh.png"));});
                if(!File.ReadAllBytes(Path.Combine(directory,kind+"-live.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(directory,kind+"-fresh.png"))))throw new InvalidOperationException("Live and fresh canvas pixels differ.");
                checks.Add(kind+" projected selection/move, one-step undo/redo and fresh-render pixel equality");
            }
            // Cancellation and non-default transforms exercise the same gesture path.
            var sticker=editor.Snapshot.Effects.Last(e=>e.Kind=="sticker");selectedEffect=sticker.Id;
            await PerformAsync(()=>editor.UpsertEffectAsync(new(){Id="p1-tilt",Kind="tilt",SourceStart=MediaTime.FromTicks(0),SourceDuration=editor.Snapshot.SourceDuration,Parameters=JsonSerializer.SerializeToElement(new{angleX=12,angleY=-18,enter=0,exit=0})}));
            foreach(int zoomIndex in new[]{0,1,3}) {
                CanvasZoom.SelectedIndex=zoomIndex;SizePreview();await preview.RenderAsync(position);QueueCanvasGeometry();await Task.Delay(120);
                var item=(await Task.Run(()=>preview.Session!.EditGeometry(position))).First(i=>i.Id==sticker.Id);
                var b=new CanvasPlane(item.Base);var rect=item.Rect;var point=b.Map(rect.X+rect.Width/2,rect.Y+rect.Height/2);
                var before=ProjectJson.Serialize(editor.Snapshot);
                canvasOverlay.Selection=sticker.Id;
                if(!canvasOverlay.BeginGesture(point.X,point.Y))throw new InvalidOperationException("Transformed canvas hit failed.");
                var target=b.Map(rect.X+rect.Width/2+0.02,rect.Y+rect.Height/2+0.02);canvasOverlay.MoveGesture(target.X,target.Y);canvasOverlay.Finish(false);await canvasCommitWork;
                if(ProjectJson.Serialize(editor.Snapshot)!=before)throw new InvalidOperationException("Cancelled gesture changed project.");
            }
            CanvasZoom.SelectedIndex=0;SizePreview();checks.Add("Tilted/rotated object hit and cancellation preserve saved state at fit/125%/200% preview");
            await PerformAsync(()=>editor.ExecuteAsync(new{op="settings",editor=(editor.Snapshot.Editor??new()) with {Crop=null}}));
            await ToggleCropEditingAsync();await Task.Delay(120);
            var crop=(await Task.Run(()=>preview.Session!.EditGeometry(position,true))).Single();var plane=new CanvasPlane(crop.Base);
            var corner=plane.Map(1,1);if(!canvasOverlay.BeginGesture(corner.X,corner.Y))throw new InvalidOperationException("Crop handle unavailable.");
            var end=plane.Map(0.8,0.75);canvasOverlay.MoveGesture(end.X,end.Y);canvasOverlay.Finish(true);await canvasCommitWork;
            if(Math.Abs(editor.Snapshot.Editor!.Crop!.Width-0.8)>0.00001)throw new InvalidOperationException("Crop resize mismatch.");
            await PerformAsync(editor.UndoAsync);await PerformAsync(editor.RedoAsync);
            await ToggleCropEditingAsync();checks.Add("Uncropped source crop handles commit back to authored cropped preview");
            await StartPlaybackAsync();await Task.Delay(500);if(!playing||preview.Session!.Status.AudioRunning==3)throw new InvalidOperationException("Streamed playback did not start.");Pause();
            checks.Add("Audio preview starts through native bounded stream");
            SetPreviewFullscreen(true);await preview.RenderAsync(position);QueueCanvasGeometry();await Task.Delay(120);
            await SaveUiImageAsync(Root,Path.Combine(directory,"fullscreen-controls.png"));SetPreviewFullscreen(false);
            await SaveUiImageAsync(Root,Path.Combine(directory,"editor-controls.png"));
            await editor.FlushAsync();var saved=ProjectJson.Deserialize(await File.ReadAllTextAsync(Path.Combine(editor.DirectoryPath,"project.json")));
            if(ProjectJson.Serialize(saved)!=ProjectJson.Serialize(editor.Snapshot))throw new InvalidOperationException("Saved canvas edits differ.");
            checks.Add("Full-screen overlay survives reparent; committed edits round-trip through project.json");success=true;
        }catch(Exception error){failure=error.ToString();}
        finally {
            Pause();await File.WriteAllTextAsync(Path.Combine(directory,"p1-validation.json"),JsonSerializer.Serialize(new{success,checks,failure},new JsonSerializerOptions{WriteIndented=true}));
            Environment.ExitCode=success?0:1;working=false;closing=false;await RequestCloseAsync();
        }
    }
}
