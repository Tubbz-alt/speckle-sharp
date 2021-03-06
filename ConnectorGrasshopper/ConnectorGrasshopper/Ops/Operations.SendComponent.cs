﻿using ConnectorGrasshopper.Extras;
using GH_IO.Serialization;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using GrasshopperAsyncComponent;
using Speckle.Core.Api;
using Speckle.Core.Credentials;
using Speckle.Core.Kits;
using Speckle.Core.Logging;
using Speckle.Core.Models;
using Speckle.Core.Transports;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Forms;
using Utilities = ConnectorGrasshopper.Extras.Utilities;

namespace ConnectorGrasshopper.Ops
{
  public class SendComponent : GH_AsyncComponent
  {
    public override Guid ComponentGuid => new Guid("{5E6A5A78-9E6F-4893-8DED-7EEAB63738A5}");

    protected override Bitmap Icon => Properties.Resources.Sender;

    public override GH_Exposure Exposure => GH_Exposure.primary;

    public bool AutoSend { get; set; } = false;

    public string CurrentComponentState { get; set; } = "needs_input";

    public bool UseDefaultCache { get; set; } = true;

    public double OverallProgress { get; set; } = 0;

    public bool JustPastedIn { get; set; }

    public List<StreamWrapper> OutputWrappers = new List<StreamWrapper>();

    public string BaseId { get; set; }

    public ISpeckleConverter Converter;

    public ISpeckleKit Kit;

    public SendComponent() : base("Send", "Send", "Sends data to a Speckle server (or any other provided transport).", "Speckle 2",
      "   Send/Receive")
    {
      BaseWorker = new SendComponentWorker(this);
      Attributes = new SendComponentAttributes(this);

      SetDefaultKitAndConverter();
    }

    public override bool Write(GH_IWriter writer)
    {
      writer.SetBoolean("UseDefaultCache", UseDefaultCache);
      writer.SetBoolean("AutoSend", AutoSend);
      writer.SetString("CurrentComponentState", CurrentComponentState);
      writer.SetString("BaseId", BaseId);
      writer.SetString("KitName", Kit.Name);

      var owSer = string.Join("\n", OutputWrappers.Select(ow => $"{ow.ServerUrl}\t{ow.StreamId}\t{ow.CommitId}"));
      writer.SetString("OutputWrappers", owSer);

      return base.Write(writer);
    }

    public override bool Read(GH_IReader reader)
    {
      UseDefaultCache = reader.GetBoolean("UseDefaultCache");
      AutoSend = reader.GetBoolean("AutoSend");
      CurrentComponentState = reader.GetString("CurrentComponentState");
      BaseId = reader.GetString("BaseId");

      var wrappersRaw = reader.GetString("OutputWrappers");
      var wrapperLines = wrappersRaw.Split('\n');
      if (wrapperLines != null && wrapperLines.Length != 0 && wrappersRaw != "")
      {
        foreach (var line in wrapperLines)
        {
          var pieces = line.Split('\t');
          OutputWrappers.Add(new StreamWrapper { ServerUrl = pieces[0], StreamId = pieces[1], CommitId = pieces[2] });
        }

        if (OutputWrappers.Count != 0)
        {
          JustPastedIn = true;
        }
      }

      var kitName = "";
      reader.TryGetString("KitName", ref kitName);

      if (kitName != "")
      {
        try
        {
          SetConverterFromKit(kitName);
        }
        catch (Exception e)
        {
          AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
            $"Could not find the {kitName} kit on this machine. Do you have it installed? \n Will fallback to the default one.");
          SetDefaultKitAndConverter();
        }
      }
      else
      {
        SetDefaultKitAndConverter();
      }

      return base.Read(reader);
    }

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
      pManager.AddGenericParameter("Data", "D", "The data to send.",
        GH_ParamAccess.tree);
      pManager.AddGenericParameter("Stream", "S", "Stream(s) and/or transports to send to.", GH_ParamAccess.tree);
      pManager.AddTextParameter("Branch", "B", "The branch you want your commit associated with.", GH_ParamAccess.tree,
        "main");
      pManager.AddTextParameter("Message", "M", "Commit message. If left blank, one will be generated for you.",
        GH_ParamAccess.tree, "");

      Params.Input[2].Optional = true;
      Params.Input[3].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
      // TODO:  Ouptut of dynamo is just a "stream", but we have several outputs here, should I kill them?
      pManager.AddGenericParameter("Stream", "S",
        "Stream or streams pointing to the created commit", GH_ParamAccess.list);
      //pManager.AddTextParameter("Object Id", "O", "The object id (hash) of the sent data.", GH_ParamAccess.list);
      //pManager.AddGenericParameter("Data", "D", "The actual sent object.", GH_ParamAccess.list);
    }

    protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
    {
      Menu_AppendSeparator(menu);
      var menuItem = Menu_AppendItem(menu, "Select the converter you want to use:");
      menuItem.Enabled = false;
      var kits = KitManager.GetKitsWithConvertersForApp(Applications.Rhino);

      foreach (var kit in kits)
      {
        Menu_AppendItem(menu, $"{kit.Name} ({kit.Description})", (s, e) => { SetConverterFromKit(kit.Name); }, true,
          kit.Name == Kit.Name);
      }

      Menu_AppendSeparator(menu);

      var cacheMi = Menu_AppendItem(menu, "Use default cache", (s, e) => UseDefaultCache = !UseDefaultCache, true,
        UseDefaultCache);
      cacheMi.ToolTipText =
        "It's advised you always use the default cache, unless you are providing a list of custom transports and you understand the consequences.";

      var autoSendMi = Menu_AppendItem(menu, "Send automatically", (s, e) =>
      {
        AutoSend = !AutoSend;
        Rhino.RhinoApp.InvokeOnUiThread((Action)delegate { OnDisplayExpired(true); });
      }, true, AutoSend);
      autoSendMi.ToolTipText =
        "Toggle automatic data sending. If set, any change in any of the input parameters of this component will start sending.\n Please be aware that if a new send starts before an old one is finished, the previous operation is cancelled.";

      if (OutputWrappers.Count != 0)
      {
        Menu_AppendSeparator(menu);
        foreach (var ow in OutputWrappers)
        {
          Menu_AppendItem(menu, $"View commit {ow.CommitId} @ {ow.ServerUrl} online ↗",
            (s, e) => System.Diagnostics.Process.Start($"{ow.ServerUrl}/streams/{ow.StreamId}/commits/{ow.CommitId}"));
        }
      }

      base.AppendAdditionalComponentMenuItems(menu);
    }

    public void SetConverterFromKit(string kitName)
    {
      if (kitName == Kit.Name) return;

      Kit = KitManager.Kits.FirstOrDefault(k => k.Name == kitName);
      Converter = Kit.LoadConverter(Applications.Rhino);

      Message = $"Using the {Kit.Name} Converter";
      ExpireSolution(true);
    }

    private void SetDefaultKitAndConverter()
    {
      Kit = KitManager.GetDefaultKit();
      try
      {
        Converter = Kit.LoadConverter(Applications.Rhino);
        Converter.SetContextDocument(Rhino.RhinoDoc.ActiveDoc);
      }
      catch
      {
        AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No default kit found on this machine.");
      }
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
      // Set output data in a "first run" event. Note: we are not persisting the actual "sent" object as it can be very big.
      if (JustPastedIn)
      {
        base.SolveInstance(DA);
        return;
      }

      if ((AutoSend || CurrentComponentState == "primed_to_send" || CurrentComponentState == "sending") &&
          !JustPastedIn)
      {
        CurrentComponentState = "sending";

        // Delegate control to parent async component.
        base.SolveInstance(DA);
        return;
      }
      else if (!JustPastedIn)
      {
        DA.SetDataList(0, OutputWrappers);
        //DA.SetData(1, BaseId);
        CurrentComponentState = "expired";
        Message = "Expired";
        OnDisplayExpired(true);
      }
    }

    public override void DisplayProgress(object sender, ElapsedEventArgs e)
    {
      if (Workers.Count == 0)
      {
        return;
      }

      Message = "";
      var total = 0.0;
      foreach (var kvp in ProgressReports)
      {
        Message += $"{kvp.Key}: {kvp.Value:0.00%}\n";
        total += kvp.Value;
      }

      OverallProgress = total / ProgressReports.Keys.Count();

      Rhino.RhinoApp.InvokeOnUiThread((Action)delegate { OnDisplayExpired(true); });
    }

    protected override void BeforeSolveInstance()
    {
      Tracker.TrackPageview("send", AutoSend ? "auto" : "manual");
      base.BeforeSolveInstance();
    }
  }

  public class SendComponentWorker : WorkerInstance
  {
    GH_Structure<IGH_Goo> DataInput;
    GH_Structure<IGH_Goo> _TransportsInput;
    GH_Structure<GH_String> _BranchNameInput;
    GH_Structure<GH_String> _MessageInput;

    string InputState;

    List<ITransport> Transports;

    Base ObjectToSend;
    long TotalObjectCount;

    Action<ConcurrentDictionary<string, int>> InternalProgressAction;

    Action<string, Exception> ErrorAction;

    List<(GH_RuntimeMessageLevel, string)> RuntimeMessages { get; set; } = new List<(GH_RuntimeMessageLevel, string)>();

    List<StreamWrapper> OutputWrappers = new List<StreamWrapper>();

    public string BaseId { get; set; }

    public SendComponentWorker(GH_Component p) : base(p)
    {
      RuntimeMessages = new List<(GH_RuntimeMessageLevel, string)>();
    }

    public override WorkerInstance Duplicate() => new SendComponentWorker(Parent);

    private System.Diagnostics.Stopwatch stopwatch;

    public override void GetData(IGH_DataAccess DA, GH_ComponentParamServer Params)
    {
      DA.GetDataTree(0, out DataInput);
      DA.GetDataTree(1, out _TransportsInput);
      DA.GetDataTree(2, out _BranchNameInput);
      DA.GetDataTree(3, out _MessageInput);

      OutputWrappers = new List<StreamWrapper>();

      stopwatch = new System.Diagnostics.Stopwatch();
      stopwatch.Start();
    }

    public override void DoWork(Action<string, double> ReportProgress, Action Done)
    {
      if (((SendComponent)Parent).JustPastedIn)
      {
        Done();
        return;
      }

      if (CancellationToken.IsCancellationRequested)
      {
        ((SendComponent)Parent).CurrentComponentState = "expired";
        return;
      }

      //the active document may have changed
      ((SendComponent)Parent).Converter.SetContextDocument(Rhino.RhinoDoc.ActiveDoc);
      var converted = Utilities.DataTreeToNestedLists(DataInput, ((SendComponent)Parent).Converter);
      ObjectToSend = new Base();
      ObjectToSend["@data"] = converted;


      TotalObjectCount = ObjectToSend.GetTotalChildrenCount();

      if (CancellationToken.IsCancellationRequested)
      {
        ((SendComponent)Parent).CurrentComponentState = "expired";
        return;
      }

      // Part 2: create transports

      Transports = new List<ITransport>();

      if (_TransportsInput.DataCount == 0)
      {
        // TODO: Set default account + "default" user stream
      }

      int t = 0;
      foreach (var data in _TransportsInput)
      {
        var transport = data.GetType().GetProperty("Value").GetValue(data);

        if (transport is string)
        {
          try
          {
            transport = new StreamWrapper(transport as string);
          }
          catch
          {
            /*we should not do this */
          }
        }

        if (transport is StreamWrapper sw)
        {
          var acc = sw.GetAccount();
          if (acc == null)
          {
            Parent.AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Could not get an account for {sw}");
            continue;
          }

          Transports.Add(new ServerTransport(acc, sw.StreamId) { TransportName = $"T{t}" });
          ;
        }
        else if (transport is ITransport otherTransport)
        {
          otherTransport.TransportName = $"T{t}";
          Transports.Add(otherTransport);
        }

        t++;
      }

      if (Transports.Count == 0)
      {
        RuntimeMessages.Add((GH_RuntimeMessageLevel.Error, "Could not identify any valid transports to send to."));
        Done();
        return;
      }

      InternalProgressAction = (dict) =>
      {
        foreach (var kvp in dict)
        {
          ReportProgress(kvp.Key, (double)kvp.Value / TotalObjectCount);
        }
      };

      ErrorAction = (transportName, exception) =>
      {
        RuntimeMessages.Add((GH_RuntimeMessageLevel.Warning, $"{transportName}: {exception.Message}"));
        var asyncParent = (GH_AsyncComponent) Parent;
        asyncParent.CancellationSources.ForEach(source => source.Cancel());
      };

      if (CancellationToken.IsCancellationRequested)
      {
        ((SendComponent)Parent).CurrentComponentState = "expired";
        return;
      }

      // Part 3: actually send stuff!

      Task.Run(async () =>
      {
        if (CancellationToken.IsCancellationRequested)
        {
          ((SendComponent)Parent).CurrentComponentState = "expired";
          return;
        }

        // Part 3.1: persist the objects
        BaseId = await Operations.Send(
          ObjectToSend,
          CancellationToken,
          Transports,
          useDefaultCache: ((SendComponent)Parent).UseDefaultCache,
          onProgressAction: InternalProgressAction,
          onErrorAction: ErrorAction);

        // 3.2 Create commits for any server transport present

        var message = _MessageInput.get_FirstItem(true).Value;
        if (message == "")
        {
          message = "Grasshopper push.";
        }

        var prevCommits = ((SendComponent)Parent).OutputWrappers;

        foreach (var transport in Transports)
        {
          if (CancellationToken.IsCancellationRequested)
          {
            ((SendComponent)Parent).CurrentComponentState = "expired";
            return;
          }

          if (!(transport is ServerTransport))
          {
            continue; // skip non-server transports (for now)
          }

          try
          {
            var client = new Client(((ServerTransport)transport).Account);
            var commitCreateInput = new CommitCreateInput
            {
              branchName = _BranchNameInput.get_FirstItem(true).Value,
              message = message,
              objectId = BaseId,
              streamId = ((ServerTransport)transport).StreamId,
            };

            // Check to see if we have a previous commit; if so set it.
            var prevCommit = prevCommits.FirstOrDefault(c =>
              c.ServerUrl == client.ServerUrl && c.StreamId == ((ServerTransport)transport).StreamId);
            if (prevCommit != null)
            {
              commitCreateInput.previousCommitIds = new List<string>() { prevCommit.CommitId };
            }

            var commitId = await client.CommitCreate(CancellationToken, commitCreateInput);

            OutputWrappers.Add(new StreamWrapper
            {
              StreamId = ((ServerTransport)transport).StreamId,
              ServerUrl = client.ServerUrl,
              CommitId = commitId
            });
          }
          catch (Exception e)
          {
            ErrorAction.Invoke("Commits", e);
          }
        }

        if (CancellationToken.IsCancellationRequested)
        {
          ((SendComponent)Parent).CurrentComponentState = "expired";
          return;
        }

        Done();
      }, CancellationToken);
    }

    public override void SetData(IGH_DataAccess DA)
    {
      stopwatch.Stop();

      if (((SendComponent)Parent).JustPastedIn)
      {
        ((SendComponent)Parent).JustPastedIn = false;
        DA.SetDataList(0, ((SendComponent)Parent).OutputWrappers);
        //DA.SetData(1, ((SendComponent)Parent).BaseId);
        return;
      }

      if (CancellationToken.IsCancellationRequested)
      {
        ((SendComponent)Parent).CurrentComponentState = "expired";
        return;
      }

      foreach (var (level, message) in RuntimeMessages)
      {
        Parent.AddRuntimeMessage(level, message);
      }

      DA.SetDataList(0, OutputWrappers);
      //DA.SetData(1, BaseId);
      //DA.SetData(2, new GH_SpeckleBase { Value = ObjectToSend });

      ((SendComponent)Parent).CurrentComponentState = "up_to_date";
      ((SendComponent)Parent).OutputWrappers =
        OutputWrappers; // ref the outputs in the parent too, so we can serialise them on write/read
      ((SendComponent)Parent).BaseId =
        BaseId; // ref the outputs in the parent too, so we can serialise them on write/read
      ((SendComponent)Parent).OverallProgress = 0;

      var hasWarnings = RuntimeMessages.Count > 0;
      if (!hasWarnings)
      {
        Parent.AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
          $"Successfully pushed {TotalObjectCount} objects to {(((SendComponent)Parent).UseDefaultCache ? Transports.Count - 1 : Transports.Count)} transports.");
        Parent.AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
          $"Send duration: {stopwatch.ElapsedMilliseconds / 1000f}s");
        foreach (var t in Transports)
        {
          if (!(t is ServerTransport st)) continue;
          var mb = st.TotalSentBytes / 1e6;
          Parent.AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
            $"{t.TransportName} avg {(mb / (stopwatch.ElapsedMilliseconds / 1000f)):0.00} MB/s");
        }
      }
    }
  }

  public class SendComponentAttributes : GH_ComponentAttributes
  {
    Rectangle ButtonBounds { get; set; }

    public SendComponentAttributes(GH_Component owner) : base(owner)
    {
    }

    protected override void Layout()
    {
      base.Layout();

      var baseRec = GH_Convert.ToRectangle(Bounds);
      baseRec.Height += 26;

      var btnRec = baseRec;
      btnRec.Y = btnRec.Bottom - 26;
      btnRec.Height = 26;
      btnRec.Inflate(-2, -2);

      Bounds = baseRec;
      ButtonBounds = btnRec;
    }

    protected override void Render(GH_Canvas canvas, Graphics graphics, GH_CanvasChannel channel)
    {
      base.Render(canvas, graphics, channel);

      var state = ((SendComponent)Owner).CurrentComponentState;

      if (channel == GH_CanvasChannel.Objects)
      {
        if (((SendComponent)Owner).AutoSend)
        {
          var autoSendButton =
            GH_Capsule.CreateTextCapsule(ButtonBounds, ButtonBounds, GH_Palette.Blue, "Auto Send", 2, 0);

          autoSendButton.Render(graphics, Selected, Owner.Locked, false);
          autoSendButton.Dispose();
        }
        else
        {
          var palette = state == "expired" ? GH_Palette.Black : GH_Palette.Transparent;
          var text = state == "sending" ? $"{((SendComponent)Owner).OverallProgress:0.00%}" : "Send";

          var button = GH_Capsule.CreateTextCapsule(ButtonBounds, ButtonBounds, palette, text, 2,
            state == "expired" ? 10 : 0);
          button.Render(graphics, Selected, Owner.Locked, false);
          button.Dispose();
        }
      }
    }

    public override GH_ObjectResponse RespondToMouseDown(GH_Canvas sender, GH_CanvasMouseEvent e)
    {
      if (e.Button == MouseButtons.Left)
      {
        if (((RectangleF)ButtonBounds).Contains(e.CanvasLocation))
        {
          if (((SendComponent)Owner).AutoSend || ((SendComponent)Owner).CurrentComponentState != "expired")
          {
            return GH_ObjectResponse.Handled;
          }

          ((SendComponent)Owner).CurrentComponentState = "primed_to_send";
          Owner.ExpireSolution(true);
          return GH_ObjectResponse.Handled;
        }
      }

      return base.RespondToMouseDown(sender, e);
    }

    public override GH_ObjectResponse RespondToMouseDoubleClick(GH_Canvas sender, GH_CanvasMouseEvent e)
    {
      // Double clicking the send button, even if the state is up to date, will do a "force send"
      if (e.Button == MouseButtons.Left)
      {
        if (((RectangleF)ButtonBounds).Contains(e.CanvasLocation))
        {
          if (((SendComponent)Owner).CurrentComponentState == "sending")
          {
            return GH_ObjectResponse.Handled;
          }

          if (((SendComponent)Owner).AutoSend)
          {
            ((SendComponent)Owner).AutoSend = false;
            Owner.OnDisplayExpired(true);
            return GH_ObjectResponse.Handled;
          }

          ((SendComponent)Owner).CurrentComponentState = "primed_to_send";
          Owner.ExpireSolution(true);
          return GH_ObjectResponse.Handled;
        }
      }

      return base.RespondToMouseDown(sender, e);
    }
  }
}
