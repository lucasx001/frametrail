using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Windows.Foundation;

namespace FrameTrail.App;

internal sealed partial class EditorTimeline
{
    protected override AutomationPeer OnCreateAutomationPeer()=>new TimelinePeer(this);
    private sealed class TimelinePeer(EditorTimeline owner):FrameworkElementAutomationPeer(owner),ISelectionProvider,IRangeValueProvider
    {
        protected override string GetClassNameCore()=>nameof(EditorTimeline);
        protected override AutomationControlType GetAutomationControlTypeCore()=>AutomationControlType.List;
        protected override string GetNameCore()=>AutomationProperties.GetName(owner);
        protected override bool IsKeyboardFocusableCore()=>true;
        protected override bool HasKeyboardFocusCore()=>owner.HasFocus;
        protected override void SetFocusCore()=>owner.FocusTimeline(FocusState.Programmatic);
        protected override object GetPatternCore(PatternInterface pattern)=>pattern is PatternInterface.Selection or PatternInterface.RangeValue?this:base.GetPatternCore(pattern);
        protected override List<AutomationPeer> GetChildrenCore()
        {
            double left=owner.scroll.HorizontalOffset,right=left+Math.Max(320,owner.ActualWidth);
            var result=owner.VisibleClips(left,right).Where(c=>owner.clipViews.ContainsKey(c.Id)).Select(c=>(AutomationPeer)new ItemPeer(this,owner,c.Id,false,c.OutputStart,c.OutputDuration,L.T("S9D4973468893",owner.clipIndices[c.Id]+1))).ToList();
            result.AddRange(owner.effects.Query(owner.T(left),owner.T(right)).Where(e=>owner.effectViews.ContainsKey(e.Id+":"+e.OutputStart)).Select(e=>(AutomationPeer)new ItemPeer(this,owner,e.Id,true,e.OutputStart,e.OutputDuration,e.Kind)));
            return result;
        }
        public bool CanSelectMultiple=>false;
        public bool IsSelectionRequired=>true;
        public IRawElementProviderSimple[] GetSelection(){
            if(owner.timeline is null)return [];
            var effect=owner.timeline.Effects.FirstOrDefault(e=>e.Id==owner.selectedVisual);
            if(effect is not null)return [ProviderFromPeer(new ItemPeer(this,owner,effect.Id,true,effect.OutputStart,effect.OutputDuration,effect.Kind))];
            var clip=owner.timeline.Clips.FirstOrDefault(c=>c.Id==owner.selected);return clip is null?[]:[ProviderFromPeer(new ItemPeer(this,owner,clip.Id,false,clip.OutputStart,clip.OutputDuration,L.T("S9D4973468893",owner.clipIndices[clip.Id]+1)))];
        }
        public bool IsReadOnly=>!owner.EditingEnabled;
        public double Minimum=>0;
        public double Maximum=>(owner.timeline?.Duration??0)/10_000_000.0;
        public double SmallChange=>1.0/60;
        public double LargeChange=>1;
        public double Value=>owner.playhead/10_000_000.0;
        public void SetValue(double value){if(IsReadOnly||!double.IsFinite(value))return;owner.HideHover();owner.Seek?.Invoke(Math.Clamp((long)(value*10_000_000),0,Math.Max(0,(owner.timeline?.Duration??1)-1)));}
        internal IRawElementProviderSimple Provider=>ProviderFromPeer(this);
    }
    private sealed class ItemPeer(TimelinePeer parent,EditorTimeline owner,string id,bool effect,long start,long duration,string label):AutomationPeer,ISelectionItemProvider,IInvokeProvider
    {
        protected override string GetNameCore()=>$"{(effect?EffectLabel(label):label)} · {TimeSpan.FromTicks(start):g} · {TimeSpan.FromTicks(duration):g}";
        protected override string GetClassNameCore()=>effect?"TimelineEffect":"TimelineClip";
        protected override AutomationControlType GetAutomationControlTypeCore()=>AutomationControlType.ListItem;
        protected override bool IsControlElementCore()=>true;
        protected override bool IsContentElementCore()=>true;
        protected override bool IsEnabledCore()=>owner.EditingEnabled;
        protected override bool IsKeyboardFocusableCore()=>true;
        protected override bool HasKeyboardFocusCore()=>owner.HasFocus&&IsSelected;
        protected override Rect GetBoundingRectangleCore()
        {
            var bounds=parent.GetBoundingRectangle();double scale=owner.XamlRoot?.RasterizationScale??1;
            double left=Math.Clamp(owner.X(start)-owner.scroll.HorizontalOffset,0,owner.ActualWidth),right=Math.Clamp(owner.X(start+duration)-owner.scroll.HorizontalOffset,0,owner.ActualWidth);
            double y=(effect?EffectY(label):32)-owner.scroll.VerticalOffset,h=effect?20:64;
            double top=Math.Clamp(y,0,owner.ActualHeight),bottom=Math.Clamp(y+h,0,owner.ActualHeight);
            return new(bounds.X+left*scale,bounds.Y+top*scale,Math.Max(0,right-left)*scale,Math.Max(0,bottom-top)*scale);
        }
        protected override Point GetClickablePointCore(){var rect=GetBoundingRectangleCore();return new(rect.X+rect.Width/2,rect.Y+rect.Height/2);}
        protected override bool IsOffscreenCore(){var rect=GetBoundingRectangleCore();return rect.Width<=0||rect.Height<=0;}
        protected override void SetFocusCore(){owner.FocusTimeline(FocusState.Programmatic);Select();}
        protected override object GetPatternCore(PatternInterface pattern)=>pattern is PatternInterface.SelectionItem or PatternInterface.Invoke?this:null!;
        public bool IsSelected=>effect?owner.selectedVisual==id:owner.selectedVisual is null&&owner.selected==id;
        public IRawElementProviderSimple SelectionContainer=>parent.Provider;
        public void Select(){if(!owner.EditingEnabled)return;if(effect){owner.selectedVisual=id;owner.SelectEffect?.Invoke(id);}else{owner.selected=id;owner.selectedVisual=null;owner.SelectClip?.Invoke(id);}owner.DescribeSelection();owner.Draw();}
        public void AddToSelection()=>Select();
        public void RemoveFromSelection(){}
        public void Invoke(){Select();owner.Seek?.Invoke(start);}
    }
}
