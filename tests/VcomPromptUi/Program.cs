using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kirin_Tool;
using Kirin_Tool.UI;
using System.Reflection;
System.Environment.SetEnvironmentVariable("KIRIN_TOOL_SETTINGS_DIR", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kirin-tool-test-settings")); AppBuilder.Configure<App>().WithInterFont().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
var owner=new MainWindow();owner.Show();
var progress=new ProgressDialog(new());var progressTask=progress.ShowDialog(owner);
Dispatcher.UIThread.RunJobs();
// Native modality is not simulated by Headless; assert dialog ownership and
// continuation behavior rather than the platform window-enabled flag.
var method=typeof(MainWindow).GetMethod("ShowGlobalInteractionPromptAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
foreach(bool done in new[]{true,false}) {
 var task=(Task<bool>)method.Invoke(owner,new object[]{"Cable replug test",progress})!;
 Dispatcher.UIThread.RunJobs();
 var prompt=progress.OwnedWindows.Single();
 var button=prompt.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,done?"Done!":"Cancel"));
 if(!button.IsEffectivelyEnabled)throw new Exception("Prompt action disabled");
 button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();
 if(!task.IsCompletedSuccessfully || task.Result!=done)throw new Exception("Prompt did not return expected result");
 if(!progress.IsEnabled)throw new Exception("Progress did not regain input");
 Console.WriteLine("PASS modal progress-owned prompt "+(done?"Done":"Cancel"));
}
// Unlock classification: the temporary loader's "already unlocked" reply is a
// no-write outcome, never a success, and an unparsable reply is not a success either.
var classify=typeof(MainWindow).GetMethod("ClassifyUnlock",BindingFlags.NonPublic|BindingFlags.Static)!;
string Classify(string output)=>classify.Invoke(null,new object[]{output})!.ToString()!;
foreach(var (output,expected) in new[]{
 ("FAILED (remote: 'already fastboot unlocked')\nfastboot: error: Command failed","AlreadyUnlockedNoWrite"),
 ("fastboot: error: Command failed\nFAILED (remote: 'already fastboot unlocked')","AlreadyUnlockedNoWrite"),
 ("< waiting for any device >\nFAILED (remote: 'already unlocked')","AlreadyUnlockedNoWrite"),
 ("OKAY [  0.005s]\nThe device will reboot and do factory reset ...","Success"),
 ("FAILED (remote: 'Command not allowed')","Failed"),
 ("(bootloader) unlocked","Unknown"),
 ("","Unknown")})
{
 var actual=Classify(output);
 if(actual!=expected)throw new Exception($"ClassifyUnlock({output.Replace("\n","\\n")}) = {actual}, expected {expected}");
}
Console.WriteLine("PASS unlock outcomes classified (already-unlocked and unparsable replies are not successes)");

// Same redline set the CLI enforces (Cli/Commands/OtaFlashRunner.cs FlashPolicy.Redline).
var redline=typeof(MainWindow).GetMethod("IsRedlinePartition",BindingFlags.NonPublic|BindingFlags.Static)!;
bool Redline(string name)=>(bool)redline.Invoke(null,new object[]{name})!;
foreach(var (name,expected) in new[]{
 ("oeminfo",true),("nvme",true),("modem_secure",true),("persist",true),
 ("modemnvm_factory",true),("certification",true),("hisee_encos",true),("veritykey",true),
 ("nvm",true),("modemst1",true),("modemst2",true),("fsg",true),
 ("boot",false),("system",false),("super",false),("ptable",false),("",false)})
{
 if(Redline(name)!=expected)throw new Exception($"IsRedlinePartition({name}) = {Redline(name)}, expected {expected}");
}
Console.WriteLine("PASS redline partition gate matches the CLI policy");

// One output classifier for every "did this command really work" question: the exit code is
// checked by the caller, this only decides what the loader actually said. Failure wins over
// success, and an output with neither token stays Unknown instead of defaulting to success.
var classifyCommand=typeof(MainWindow).GetMethod("ClassifyCommandOutput",BindingFlags.NonPublic|BindingFlags.Static)!;
string Command(string output)=>classifyCommand.Invoke(null,new object[]{output})!.ToString()!;
foreach(var (output,expected) in new[]{
 ("OKAY [  0.005s]\nFinished. Total time: 0.006s","Succeeded"),
 ("FAILED (remote: 'Command not allowed')\nfastboot: error: Command failed","Failed"),
 ("FAILED (remote: 'not support')\nOKAY [  0.001s]","Failed"),
 ("(bootloader) product: els-an00\n","Unknown"),
 ("","Unknown")})
{
 var actual=Command(output);
 if(actual!=expected)throw new Exception($"ClassifyCommandOutput({output.Replace("\n","\\n")}) = {actual}, expected {expected}");
}
Console.WriteLine("PASS command output classified (no OKAY and no ERROR is not success)");

// rescue_enter_recovery: the loader banners are corroborating evidence, not the only judge.
var classifyMode=typeof(MainWindow).GetMethod("ClassifyUsbUpdateModeEntry",BindingFlags.NonPublic|BindingFlags.Static)!;
string ModeEntry(string output)=>classifyMode.Invoke(null,new object[]{output})!.ToString()!;
foreach(var (output,expected) in new[]{
 ("OKAY [  1.000s]","Entered"),
 ("SUCCESS","Entered"),
 ("This is sec phone!!","Entered"),
 ("start to hisuite","Entered"),
 ("FAILED (remote: 'Command not allowed')","Failed"),
 ("FAILED (remote: 'Command not allowed')\nThis is sec phone!!","Failed"),
 ("< waiting for any device >","Unknown"),
 ("","Unknown")})
{
 var actual=ModeEntry(output);
 if(actual!=expected)throw new Exception($"ClassifyUsbUpdateModeEntry({output.Replace("\n","\\n")}) = {actual}, expected {expected}");
}
Console.WriteLine("PASS USB Update mode entry classified (Unknown is not reported as success)");

// The dead List<PartitionInfo> overload was removed; the live 5-parameter overload stays.
var flashOverloads=typeof(MainWindow).GetMethods(BindingFlags.Instance|BindingFlags.NonPublic)
    .Where(m=>m.Name=="FlashPartitionsWithProgress").ToArray();
if(flashOverloads.Length!=1||flashOverloads[0].GetParameters().Length!=5)
    throw new Exception($"expected exactly one 5-parameter FlashPartitionsWithProgress, found {flashOverloads.Length}");
Console.WriteLine("PASS dead FlashPartitionsWithProgress(List<PartitionInfo>) overload removed");

progress.Close();owner.Close();
