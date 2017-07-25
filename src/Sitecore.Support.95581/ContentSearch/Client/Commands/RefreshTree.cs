// --------------------------------------------------------------------------------------------------------------------
// <copyright file="RefreshTree.cs" company="Sitecore">
//   Copyright (c) Sitecore. All rights reserved.
// </copyright>
// <summary>
//   Represents the search:refreshtree command.
// </summary>
// --------------------------------------------------------------------------------------------------------------------

using System.Collections.Specialized;

using Sitecore.Data;
using Sitecore.Web.UI.Sheer;

namespace Sitecore.Support.ContentSearch.Client.Commands
{
  using System;
  using System.Linq;
  using System.Threading;
  using Sitecore.Abstractions;
  using Sitecore.ContentSearch.Commands;
  using Sitecore.ContentSearch.Maintenance;
  using Sitecore.Data.Items;
  using Sitecore.Diagnostics;
  using Sitecore.Jobs;
  using Sitecore.Shell.Applications.Dialogs.ProgressBoxes;
  using Sitecore.Shell.Framework.Commands;
  using Sitecore.ContentSearch;
  using Sitecore.ContentSearch.Diagnostics;
  using System.Collections.Generic;

  /// <summary>
  /// Represents the search:refreshtree command.
  /// </summary>
  [Serializable]
  public class RefreshTree : Command, IContentSearchCommand
  {
    /// <summary>
    /// The translate.
    /// </summary>
    private ITranslate translate;

    /// <summary>
    /// The event.
    /// </summary>
    private IEvent @event;

    #region Construction

    /// <summary>
    /// Initializes a new instance of the <see cref="RefreshTree"/> class.
    /// </summary>
    public RefreshTree()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RefreshTree"/> class.
    /// </summary>
    /// <param name="translate">
    /// The translate.
    /// </param>
    internal RefreshTree(ITranslate translate)
    {
      this.translate = translate;
    }

    #endregion

    #region Properties

    /// <summary>Gets or sets the reference to the UI job that ProgressBox creates</summary>
    protected Handle JobHandle { get; set; }

    /// <summary>
    /// Gets the event hub.
    /// </summary>
    protected IEvent Event
    {
      get
      {
        if (this.@event == null)
        {
          Interlocked.CompareExchange(
              ref this.@event,
              ContentSearchManager.Locator.GetInstance<IEvent>(),
              null);
        }

        return this.@event;
      }
    }

    /// <summary>
    /// Gets the translation API.
    /// </summary>
    protected ITranslate Translate
    {
      get
      {
        if (this.translate == null)
        {
          Interlocked.CompareExchange(
              ref this.translate,
              ContentSearchManager.Locator.GetInstance<ITranslate>(),
              null);
        }

        return this.translate;
      }
    }

    #endregion

    #region Public methods

    /// <summary>
    /// Executes the command in the specified context.
    /// </summary>
    /// <param name="context">The context.</param>
    public override void Execute([NotNull] CommandContext context)
    {
      Assert.ArgumentNotNull(context, "context");

      var item = context.Items[0];
      Assert.IsNotNull(item, "context item cannot be null");

      Context.ClientPage.Start(
          this,
          "Run",
          new NameValueCollection { { "itemUri", item.Uri.ToString() }, { "itemPath", item.Paths.ContentPath } });
    }

    /// <summary>
    /// Runs re-indexing in ProgressBox.
    /// </summary>
    /// <param name="args">
    /// The args.
    /// </param>
    protected void Run(ClientPipelineArgs args)
    {
      string itemPath = args.Parameters["itemPath"];
      if (string.IsNullOrEmpty(itemPath))
      {
        return;
      }

      var jobName = string.Format("{0} ({1})", this.Translate.Text(Sitecore.ContentSearch.Localization.Texts.ReIndexTree), itemPath);
      var headerText = this.Translate.Text(Sitecore.ContentSearch.Localization.Texts.ReIndexTreeHeader);
      ProgressBox.ExecuteSync(jobName, headerText, "Applications/16x16/replace2.png", this.Refresh, this.RefreshDone);
    }

    /// <summary>
    /// Gets item by its uri.
    /// </summary>
    /// <param name="uri">
    /// The Item URI.
    /// </param>
    /// <returns>
    /// The <see cref="Item"/>.
    /// </returns>
    private static Item GetItemByUri(string uri)
    {
      var itemUri = ItemUri.Parse(uri);
      Database db = ContentSearchManager.Locator.GetInstance<IFactory>().GetDatabase(itemUri.DatabaseName);
      Item item = db.GetItem(itemUri.ToDataUri());
      return item;
    }

    /// <summary>
    /// The refresh.
    /// </summary>
    /// <param name="args">
    /// The client Pipeline Args.
    /// </param>
    private void Refresh(ClientPipelineArgs args)
    {
      this.JobHandle = Context.Job.Handle;

      Item item = GetItemByUri(args.Parameters["itemUri"]);

      if (item == null)
      {
        return;
      }

      this.Event.Subscribe("indexing:updateditem", this.ShowProgress);

      var jobs = GetRefreshTreeJobs((SitecoreIndexableItem)item, new[] { IndexGroup.Experience }).ToList(); // Sitecore.Support.95581

      while (jobs.Any(j => !j.IsDone))
      {
        Thread.Sleep(500);
      }

      this.Event.Unsubscribe("indexing:updateitem", this.ShowProgress);

      Job failedJob = jobs.FirstOrDefault(j => j.Status.Failed);
      if (failedJob != null)
      {
        args.Parameters["failed"] = "1";
      }
    }

    /// <summary>
    /// Event handler to show updating progress.
    /// </summary>
    /// <param name="sender">
    /// The sender.
    /// </param>
    /// <param name="eventArgs">
    /// The event args.
    /// </param>
    private void ShowProgress(object sender, EventArgs eventArgs)
    {
      object[] parameters = this.Event.ExtractParameters(eventArgs);

      if (parameters == null || parameters.Length < 3)
      {
        return;
      }

      var itemPath = parameters[2] as string;

      if (!string.IsNullOrEmpty(itemPath) && this.JobHandle != null)
      {
        var job = JobManager.GetJob(this.JobHandle);
        if (job != null)
        {
          job.Status.Messages.Add(itemPath);
        }
      }
    }

    /// <summary>
    /// Invoked when refresh done.
    /// </summary>
    /// <param name="args">
    /// The args.
    /// </param>
    private void RefreshDone(ClientPipelineArgs args)
    {
      var message =
          this.Translate.Text(
              args.Parameters["failed"] == "1"
                  ? Sitecore.ContentSearch.Localization.Texts.ReindexTreeFailed
                  : Sitecore.ContentSearch.Localization.Texts.ReIndexTreeComplete);

      SheerResponse.Alert(message);
    }


    #endregion

    #region Sitecore.Support.95581
    /// <summary>Refresh all indexes at a specific starting point.</summary>
    /// <param name="startItem">The start item.</param>
    /// <returns></returns>
    public static IEnumerable<Job> GetRefreshTreeJobs(IIndexable startItem, IndexGroup[] indexGroupsToSkip = null)
    {
      Assert.ArgumentNotNull(startItem, "startItem");

      if (indexGroupsToSkip != null)
      {
        CrawlingLog.Log.Debug(string.Format("IndexCustodian. RefreshTree triggered on item '{0}'. Index groups to skip: '{1}'.", startItem.AbsolutePath, string.Join(", ", indexGroupsToSkip.Select(g => g.Name))));

        return ContentSearchManager.Indexes.Where(index => !indexGroupsToSkip.Contains(index.Group)).Select(index => IndexCustodian.Refresh(index, startItem));
      }
      CrawlingLog.Log.Debug(string.Format("IndexCustodian. RefreshTree triggered on item '{0}'.", startItem.AbsolutePath));

      return ContentSearchManager.Indexes.Select(index => IndexCustodian.Refresh(index, startItem));
    }

    #endregion

  }
}
