using System.IO;

namespace BeeX.DeskNest;

public sealed partial class DeskNestService
{
    void ShowTodoReminderToast(string title,string message)
    {
        try
        {
            // Ensure Toast notifications are registered (requires a Start Menu shortcut)
            if(!toastReady){try{TryRegisterToastActivator();toastReady=true;}catch{return;}}
            new Microsoft.Toolkit.Uwp.Notifications.ToastContentBuilder()
                .AddText(title)
                .AddText(message)
                .Show();
        }
        catch{}
    }
    bool toastReady;
    void TryRegisterToastActivator()
    {
        var programsDir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),"Programs","BeeX");
        var shortcutPath=Path.Combine(programsDir,"BeeX DeskNest.lnk");
        if(File.Exists(shortcutPath))return;
        var exePath=Environment.ProcessPath??"";
        if(string.IsNullOrEmpty(exePath)||!File.Exists(exePath))return;
        try
        {
            Directory.CreateDirectory(programsDir);
            dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic shortcut=shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath=exePath;
            shortcut.WorkingDirectory=Path.GetDirectoryName(exePath)??"";
            shortcut.Save();
        }
        catch{}
    }
    void CheckReminders(){if(DateTime.Now.Second>2)return;var now=DateTime.Now;foreach(var todo in State.Nests.SelectMany(n=>n.Todos).Where(t=>!t.Done&&!t.ReminderDismissed&&t.DueAt.HasValue&&!reminded.Contains(t.Id)).ToList()){int? offset=null;if(todo.SnoozeUntil.HasValue){if(todo.SnoozeUntil>now)continue;todo.SnoozeUntil=null;offset=-1;}else{var crossed=todo.ReminderOffsets.Distinct().Where(x=>!todo.SentReminderOffsets.Contains(x)&&now>=todo.DueAt!.Value.AddMinutes(-x)).ToList();if(crossed.Count==0)continue;offset=crossed.OrderBy(x=>Math.Abs((todo.DueAt!.Value-now).TotalMinutes-x)).First();foreach(var item in crossed)todo.SentReminderOffsets.Add(item);}reminded.Add(todo.Id);Save();if(sessionLocked){ShowTodoReminderToast("BeeX 待辦提醒",State.Language=="zh-CN"?"你有一个待办提醒，解锁后查看详情。":State.Language=="en-US"?"You have a todo reminder. Unlock to view details.":"你有一個待辦提醒，解鎖後查看詳情。");reminded.Remove(todo.Id);continue;}var reminder=new ReminderWindow(todo,offset.Value,State,()=>{if(!AdvanceRepeatingTodo(todo)){todo.Done=true;todo.ReminderDismissed=true;}todo.SnoozeUntil=null;reminded.Remove(todo.Id);Save();foreach(var w in windows.Values)w.RefreshData();},minutes=>{todo.SnoozeUntil=DateTime.Now.AddMinutes(minutes);reminded.Remove(todo.Id);Save();},()=>{todo.SnoozeUntil=null;reminded.Remove(todo.Id);Save();});reminder.Show();}}
    static bool AdvanceRepeatingTodo(TodoItem todo)
    {
        if(string.IsNullOrWhiteSpace(todo.Repeat)||todo.Repeat=="不重複"||!todo.DueAt.HasValue)return false;
        var next=todo.DueAt.Value;
        var now=DateTime.Now;
        for(var guard=0;guard<400&&next<=now;guard++)next=todo.Repeat switch{"每天"=>next.AddDays(1),"每週"=>next.AddDays(7),"每兩週"=>next.AddDays(14),"每月"=>next.AddMonths(1),_=>DateTime.MinValue};
        if(next==DateTime.MinValue)return false;
        todo.DueAt=next;
        todo.Done=false;
        todo.ReminderDismissed=false;
        todo.SnoozeUntil=null;
        todo.SentReminderOffsets.Clear();
        todo.DeadlineNotice1DaySent=false;
        todo.DeadlineNotice2DaysSent=false;
        return true;
    }
}
