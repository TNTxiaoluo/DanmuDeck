namespace BiliXbox.Controls
{
    public static class AuxiliaryTests
    {
        private static int _checks;
        private static void Check(bool value,string name) {++_checks;if(!value)throw new System.Exception(name);}
        private static System.TimeSpan Time(int ms)=>System.TimeSpan.FromMilliseconds(ms);
        public static void Run()
        {
            var state=new GamepadAuxiliaryState();var device=new object();
            var f=state.Sample(Time(0),device,false,false,0,true);
            Check(f.SectionStep==0&&f.ScrollPixels==0,"initial sample is neutral");
            state.Sample(Time(33),device,false,false,0,true);
            f=state.Sample(Time(66),device,false,true,0,true);Check(f.SectionStep==1,"RB switches once");
            for(int i=3;i<=40;i++){f=state.Sample(Time(i*33),device,false,true,0,true);Check(f.SectionStep==0,"held RB cannot repeat");}
            state.Sample(Time(1400),device,false,false,0,true);
            Check(state.Sample(Time(1433),device,true,false,0,true).SectionStep==-1,"LB switches left after release");
            Check(state.Sample(Time(1466),device,true,true,0,true).SectionStep==0,"opposite bumpers cancel");
            state.Sample(Time(1500),device,false,false,0,true);
            Check(state.Sample(Time(1533),device,false,true,0,true).SectionStep==1,"rapid separate presses retained");
            state.Sample(Time(1566),device,false,false,0,true);
            Check(state.Sample(Time(1599),device,false,false,.2,true).ScrollPixels==0,"stick drift deadzone");
            double small=state.Sample(Time(1632),device,false,false,-.4,true).ScrollPixels;
            double large=state.Sample(Time(1665),device,false,false,-1,true).ScrollPixels;
            Check(small>0&&large>small,"downward scroll scales with deflection");
            Check(state.Sample(Time(1698),device,false,false,1,true).ScrollPixels<0,"upward stick scrolls up");
            Check(System.Math.Abs(state.Sample(Time(10000),device,false,false,-1,true).ScrollPixels)<=46.001,"stalled frames do not catch up in a burst");
            f=state.Sample(Time(10033),device,false,true,-1,false);Check(f.SectionStep==0&&f.ScrollPixels==0,"dialog blocks all auxiliary input");
            f=state.Sample(Time(10066),device,false,true,-1,true);Check(f.SectionStep==0&&f.ScrollPixels==0,"held input blocked when dialog closes");
            f=state.Sample(Time(10099),device,false,true,-1,true);Check(f.SectionStep==0&&f.ScrollPixels==0,"stick stays blocked until neutral");
            state.Sample(Time(10132),device,false,false,0,true);
            f=state.Sample(Time(10165),device,false,true,-1,true);Check(f.SectionStep==1&&f.ScrollPixels>0,"release rearms both inputs");
            f=state.Sample(Time(10198),new object(),true,false,-1,true);Check(f.SectionStep==0&&f.ScrollPixels==0,"new controller cannot replay held input");
            state.Reset();state.Sample(Time(0),device,false,false,0,true);state.Sample(Time(33),device,false,false,0,true);
            Check(state.Sample(Time(66),device,false,false,double.NaN,true).ScrollPixels==0,"invalid analog reading rejected");
            Check(state.Sample(Time(99),device,false,false,-1,true).ScrollPixels==0,"invalid read requires neutral");
            state.Sample(Time(132),device,false,false,0,true);Check(state.Sample(Time(165),device,false,false,-1,true).ScrollPixels>0,"recovers from invalid read");
            bool threw=false;try{state.Sample(Time(164),device,false,false,0,true);}catch(System.ArgumentOutOfRangeException){threw=true;}
            Check(threw,"monotonic time enforced");
            System.Console.WriteLine("PASS: "+_checks+" production bumper/right-stick sampling assertions.");
        }
    }
}
