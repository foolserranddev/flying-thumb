namespace FlyingThumbManager;

public sealed class TransferWindow : Form
{
    readonly Label detail = new() { Dock=DockStyle.Fill,AutoEllipsis=true };
    readonly Label queued = new() { Dock=DockStyle.Fill };
    readonly ProgressBar progress = new() { Dock=DockStyle.Fill,Maximum=1000 };
    readonly Button cancel = new() { Text="Cancel transfer",AutoSize=true };
    bool finished;
    public event EventHandler? CancelRequested;
    public TransferWindow()
    {
        Text="Flying Thumb — Transferring files"; Font=new Font("Segoe UI",10); AutoScaleMode=AutoScaleMode.Dpi;
        var unit=DeviceDpi/96f;
        ClientSize=new Size((int)(520*unit),(int)(260*unit)); MinimumSize=new Size((int)(460*unit),(int)(300*unit)); StartPosition=FormStartPosition.CenterParent;
        MaximizeBox=false; MinimizeBox=true;
        var layout=new TableLayoutPanel { Dock=DockStyle.Fill,Padding=new Padding((int)(18*unit)),ColumnCount=1,RowCount=5 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,42*unit)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,26*unit)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,32*unit)); layout.RowStyles.Add(new RowStyle(SizeType.Percent,100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,36*unit));
        var warning=new Label { Text="Do not remove the drive or close the app while transferring.\nYou can keep browsing and add more files to the queue.",Dock=DockStyle.Fill };
        var buttons=new FlowLayoutPanel { Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft }; buttons.Controls.Add(cancel);
        layout.Controls.Add(detail);layout.Controls.Add(progress);layout.Controls.Add(queued);layout.Controls.Add(warning);layout.Controls.Add(buttons);Controls.Add(layout);
        cancel.Click+=(_,_)=>RequestCancel();
        FormClosing+=(_,e)=> { if(!finished) { e.Cancel=true; RequestCancel(); } };
    }
    void RequestCancel() { if(!cancel.Enabled)return; cancel.Enabled=false;detail.Text="Cancelling; finishing drive cleanup...";CancelRequested?.Invoke(this,EventArgs.Empty); }
    public void UpdateProgress(string text,int value,int queueCount) { if(!cancel.Enabled)return;detail.Text=text;progress.Value=Math.Clamp(value,0,1000);queued.Text=queueCount==0?"No additional transfers queued":$"{queueCount} additional transfer(s) queued"; }
    public void Finish() { finished=true;Close();Dispose(); }
}
