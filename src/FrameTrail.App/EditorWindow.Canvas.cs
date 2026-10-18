using FrameTrail.Domain;
using FrameTrail.Interop;
using Microsoft.UI.Xaml;
using System.Text.Json;

namespace FrameTrail.App;

public sealed partial class EditorWindow
{
    private readonly EditorCanvasOverlay canvasOverlay = new();
    private bool cropEditing, canvasTransaction;
    private Task canvasRenderWork = Task.CompletedTask, canvasCommitWork = Task.CompletedTask;
    private ProjectSnapshot? canvasPending;
    private int geometryRequest;
    private void InitializeCanvasEditing()
    {
        PreviewMount.Children.Add(canvasOverlay);
        canvasOverlay.Selected += id => { if(id!="crop"){selectedEffect=id;ShowInspector(2);RefreshVisualInspector();} };
        canvasOverlay.Begin += () => {
            if(working||playing||editor.IsExporting||visualPending||visualTransaction||!canvasCommitWork.IsCompleted){canvasOverlay.Finish(false);return;}
            editor.BeginTransaction();canvasTransaction=true;timeline.EditingEnabled=false;
        };
        canvasOverlay.Changed += (id,rect) => {
            if(!canvasTransaction)return;
            try {
                if(id=="crop") canvasPending=editor.PreviewCommand(new {op="settings",editor=(editor.Snapshot.Editor??new()) with {Crop=rect}});
                else {
                    var effect=editor.Snapshot.Effects.First(e=>e.Id==id);
                    var p=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(effect.Parameters.GetRawText())!;
                    p["x"]=JsonSerializer.SerializeToElement(rect.X);p["y"]=JsonSerializer.SerializeToElement(rect.Y);
                    p["width"]=JsonSerializer.SerializeToElement(rect.Width);p["height"]=JsonSerializer.SerializeToElement(rect.Height);
                    canvasPending=editor.PreviewCommand(new {op="effectUpsert",effectId=id,effect=effect with {Parameters=JsonSerializer.SerializeToElement(p)}});
                }
                if(canvasRenderWork.IsCompleted)canvasRenderWork=RenderCanvasDraftsAsync();
            } catch(Exception error){canvasOverlay.Finish(false);ShowNotice("无法调整画布对象",error.Message);}
        };
        canvasOverlay.Ended += commit => { if(canvasTransaction)canvasCommitWork=CommitCanvasAsync(commit); };
        PreviewMount.SizeChanged += (_,_)=>QueueCanvasGeometry();
    }
    private ProjectSnapshot CanvasDisplay(ProjectSnapshot p) => cropEditing ? p with {
        Editor=(p.Editor??new()) with {Crop=null,FrameStyle="none"}, Effects=[],
        Canvas=p.Canvas with {Camera=p.Canvas.Camera with {Enabled=false}}
    } : p;
    private async Task RenderCanvasDraftsAsync()
    {
        try { while(canvasPending is { } p){canvasPending=null;await preview.UpdateProjectAsync(CanvasDisplay(p),position);} }
        catch(OperationCanceledException) { }
        catch(Exception error){ShowNotice("画布预览未完成",error.Message);}
    }
    private async Task CommitCanvasAsync(bool commit)
    {
        working=true;var before=editor.Snapshot;
        try {
            await canvasRenderWork;
            if(commit)await editor.CommitTransactionAsync();else editor.CancelTransaction();canvasTransaction=false;
            await UpdateEditedProjectAsync(before,null);
            if(cropEditing)await preview.UpdateProjectAsync(CanvasDisplay(editor.Snapshot),position);
        } catch(Exception error){editor.CancelTransaction();canvasTransaction=false;ShowNotice("画布修改未完成",error.Message);}
        finally {working=false;timeline.EditingEnabled=true;UpdateState();QueueCanvasGeometry();}
    }
    private async void QueueCanvasGeometry()
    {
        int request=++geometryRequest;
        if(!ready||closing||canvasOverlay.Dragging||canvasTransaction)return;
        if(playing||working||editor.IsExporting||visualPending||visualTransaction){canvasOverlay.SetItems([]);return;}
        try {
            var media=preview.Session;if(media is null)return;
            var items=await Task.Run(()=>media.EditGeometry(position,cropEditing));
            if(request!=geometryRequest||closing||canvasOverlay.Dragging||working)return;
            if(cropEditing && items.FirstOrDefault() is { } item){
                var r=editor.Snapshot.Editor?.Crop??new NormalizedRect();var plane=new CanvasPlane(item.Base);
                var q=new[]{plane.Map(r.X,r.Y),plane.Map(r.X+r.Width,r.Y),plane.Map(r.X+r.Width,r.Y+r.Height),plane.Map(r.X,r.Y+r.Height)};
                items=new[]{item with {Rect=r,Quad=q.Select(v=>new[]{v.X,v.Y}).ToArray()}};
            }
            canvasOverlay.Selection=cropEditing?"crop":selectedEffect;canvasOverlay.SetItems(items);
        } catch(OperationCanceledException) { }
        catch(ObjectDisposedException) { }
        catch(Exception error){SafeDiagnostics.Record(error);canvasOverlay.SetItems([]);}
    }
    private async Task ToggleCropEditingAsync()
    {
        if(working||closing||editor.IsExporting)return;
        Pause();canvasOverlay.Finish(true);await canvasCommitWork;await FlushVisualChangesAsync();await FlushPictureInputsAsync();
        cropEditing=!cropEditing;ShowInspector(0);SetFramePage(true);
        await preview.UpdateProjectAsync(CanvasDisplay(editor.Snapshot),position);QueueCanvasGeometry();
        if(cropEditing)ShowNotice("拖动画面裁切框", "拖动边角调整裁切范围，拖动框内移动。再次点击裁切或按 Esc 返回成片预览。");
    }
}
