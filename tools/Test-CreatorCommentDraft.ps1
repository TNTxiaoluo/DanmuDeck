#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$production = [IO.File]::ReadAllText((Join-Path $root 'src\BiliXbox\Controls\CreatorCommentDraftState.cs'))
$tests = @'
namespace BiliXbox.Controls {
 public static class CreatorDraftTests {
  public static void Run() {
   int checks=0;
   System.Action<bool,string> check=(value,label)=>{ if(!value) throw new System.Exception(label); ++checks; };
   var state=new CreatorCommentDraftState();
   check(state.Begin()==0,"Empty draft cannot be posted");
   state.Edit("original"); long first=state.Begin();
   check(first>0 && state.InFlight,"Submission locks the target");
   check(state.Begin()==0,"Duplicate A cannot submit while in flight");
   check(!state.ConfirmRetry(),"Pending submission cannot be rearmed");
   state.Edit("newer draft");
   check(state.Complete(first,true,"submitted"),"Current response accepted");
   check(state.Draft=="newer draft","Late success must preserve an edited draft");
   check(!state.InFlight && !state.IsUncertain,"Confirmed submission releases lock");
   long second=state.Begin();
   check(second>first,"Attempts have distinct generations");
   check(!state.Complete(first,false,"stale"),"Stale response cannot unlock newer submission");
   check(state.InFlight,"Newer pending submission remains locked");
   check(state.Complete(second,false,"unknown after cancellation"),"Cancelled transport records uncertainty");
   check(state.Draft=="newer draft" && state.IsUncertain,"Unconfirmed response preserves draft");
   check(state.Begin()==0,"Returning to the dynamic cannot silently retry");
   check(state.ConfirmRetry(),"User can explicitly arm a retry");
   state.DisarmRetry();
   check(state.Begin()==0,"Navigation removes retry confirmation");
   state.ConfirmRetry(); long third=state.Begin();
   check(third>second && state.InFlight,"Second deliberate confirmation submits a retry");
   check(state.Complete(third,true,"submitted") && state.Draft=="","Confirmed unchanged draft clears");
   check(!state.Complete(third,false,"duplicate"),"Repeated completion cannot change success");
   var otherAccount=new CreatorCommentDraftState();
   check(otherAccount.Draft=="" && !otherAccount.InFlight,"Separate account target state is isolated");
   System.Console.WriteLine("PASS: "+checks+" production creator comment draft checks.");
  }
 }
}
'@
Add-Type -TypeDefinition ($production + [Environment]::NewLine + $tests) -CompilerOptions '/langversion:7.3'
[BiliXbox.Controls.CreatorDraftTests]::Run()
