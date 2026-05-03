using DataContainer.Generated;
using Dignus.Collections;
using Dignus.DependencyInjection.Attributes;
using Dignus.Log;
using Macro.Infrastructure.Manager;
using Macro.Models;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Utils;
using Utils.Infrastructure;

namespace Macro.Infrastructure.Controller
{
    internal class SearchResult
    {
        public int Similarity { get; set; }
        public Point2D Location { get; set; }
        public bool UseRoi { get; set; }
        public IntRect RoiRect { get; set; }
    }

    [Injectable(Dignus.DependencyInjection.LifeScope.Transient)]
    internal class BatchModeController : MacroModeControllerBase
    {
        private readonly InputEventExecutor _eventProcessorHandler;
        private readonly CacheDataManager _cacheDataManager;

        public BatchModeController(Config config,
            InputEventExecutor inputEventProcessorHandler,
            CacheDataManager cacheDataManager) : base(config)
        {
            _eventProcessorHandler = inputEventProcessorHandler;
            _cacheDataManager = cacheDataManager;
        }

        public override void Execute(
            ArrayQueue<Process> processes,
            ArrayQueue<EventInfoModel> eventInfoModels,
            CancellationToken cancellationToken)
        {
            for (int i = 0; i < processes.Count; ++i)
            {
                var process = processes[i];
                ProcessEventInfos(process, eventInfoModels, cancellationToken);
                TaskHelper.TokenCheckDelay(_config.ProcessPeriod, cancellationToken);
            }
        }

        private Bitmap _currentSourceBmp;
        private Process _currentProcess;

        private void ProcessEventInfos(
            Process process,
            ArrayQueue<EventInfoModel> eventInfoModels,
            CancellationToken cancellationToken)
        {
            if (_screenCaptureManager.CaptureProcessWindow(process,
                    out Bitmap sourceBmp) == false)
            {
                return;
            }

            _currentSourceBmp = sourceBmp;
            _currentProcess = process;

            try
            {
                var results = SearchAllEventItems(sourceBmp, eventInfoModels, cancellationToken);

                if (_config.SearchImageResultDisplay)
                {
                    DrawAllResults(sourceBmp, eventInfoModels, results);
                }

                Draw(sourceBmp);

                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                ProcessMatchedEvents(process, eventInfoModels, results, cancellationToken);
            }
            finally
            {
                _currentSourceBmp?.Dispose();
                _currentSourceBmp = null;
            }
        }

        private ConcurrentDictionary<EventInfoModel, SearchResult> SearchAllEventItems(
            Bitmap sourceBmp,
            ArrayQueue<EventInfoModel> eventInfoModels,
            CancellationToken cancellationToken)
        {
            var results = new ConcurrentDictionary<EventInfoModel, SearchResult>();

            if (_config.EnableParallelSearch && eventInfoModels.Count > 1)
            {
                var options = new ParallelOptions
                {
                    MaxDegreeOfParallelism = _config.ParallelSearchDegree > 0
                        ? _config.ParallelSearchDegree
                        : Environment.ProcessorCount
                };

                Parallel.ForEach(eventInfoModels, options, (model, loopState) =>
                {
                    if (cancellationToken.IsCancellationRequested || loopState.ShouldExitCurrentIteration)
                    {
                        loopState.Stop();
                        return;
                    }

                    var result = SearchSingleItem(sourceBmp, model);
                    results[model] = result;
                });
            }
            else
            {
                foreach (var model in eventInfoModels)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    var result = SearchSingleItem(sourceBmp, model);
                    results[model] = result;
                }
            }

            return results;
        }

        private SearchResult SearchSingleItem(Bitmap sourceBmp, EventInfoModel model)
        {
            var result = new SearchResult();
            result.UseRoi = model.RoiDataInfo.IsExists();

            if (result.UseRoi)
            {
                var newRect = _screenCaptureManager.AdjustRectForDPI(model.RoiDataInfo.RoiRect, model.RoiDataInfo.MonitorInfo);

                int imageWidth = sourceBmp.Width;
                int imageHeight = sourceBmp.Height;

                if (newRect.Left < 0 || newRect.Right > imageWidth || newRect.Top < 0 || newRect.Bottom > imageHeight)
                {
                    newRect.Left = 0;
                    newRect.Right = imageWidth;
                    newRect.Top = 0;
                    newRect.Bottom = imageHeight;
                }
                else
                {
                    newRect.Left = Math.Max(0, Math.Min(newRect.Left, imageWidth - 1));
                    newRect.Right = Math.Max(newRect.Left + 1, Math.Min(newRect.Right, imageWidth - 1));
                    newRect.Top = Math.Max(0, Math.Min(newRect.Top, imageHeight - 1));
                    newRect.Bottom = Math.Max(newRect.Top + 1, Math.Min(newRect.Bottom, imageHeight - 1));
                }

                result.RoiRect = newRect;

                Bitmap roiBmp = null;
                try
                {
                    roiBmp = OpenCVHelper.CropImage(sourceBmp, newRect);
                }
                catch (Exception ex)
                {
                    LogHelper.Error(ex);
                }

                if (roiBmp != null)
                {
                    var searchResult = OpenCVHelper.SearchOnly(roiBmp, model.Image);
                    result.Similarity = searchResult.Item1;
                    result.Location = new Point2D(
                        searchResult.Item2.X + newRect.Left,
                        searchResult.Item2.Y + newRect.Top);
                }
            }
            else
            {
                var searchResult = OpenCVHelper.SearchOnly(sourceBmp, model.Image);
                result.Similarity = searchResult.Item1;
                result.Location = searchResult.Item2;
            }

            return result;
        }

        private void DrawAllResults(
            Bitmap sourceBmp,
            ArrayQueue<EventInfoModel> eventInfoModels,
            ConcurrentDictionary<EventInfoModel, SearchResult> results)
        {
            using (var g = Graphics.FromImage(sourceBmp))
            {
                using (var pen = new Pen(Color.Red, 2))
                {
                    foreach (var model in eventInfoModels)
                    {
                        if (results.TryGetValue(model, out var result) &&
                            result.Similarity >= _config.Similarity)
                        {
                            g.DrawRectangle(pen,
                                new System.Drawing.Rectangle
                                {
                                    X = (int)result.Location.X,
                                    Y = (int)result.Location.Y,
                                    Width = model.Image.Width,
                                    Height = model.Image.Height
                                });
                        }
                    }
                }
            }
        }

        private void ProcessMatchedEvents(
            Process process,
            ArrayQueue<EventInfoModel> eventInfoModels,
            ConcurrentDictionary<EventInfoModel, SearchResult> results,
            CancellationToken cancellationToken)
        {
            for (int i = 0; i < eventInfoModels.Count; ++i)
            {
                var model = eventInfoModels[i];

                if (!results.TryGetValue(model, out var result))
                {
                    continue;
                }

                if (result.Similarity < _config.Similarity)
                {
                    TaskHelper.TokenCheckDelay(_config.ItemDelay, cancellationToken);
                    continue;
                }

                var handleResult = HandleEvent(process, model, result, cancellationToken);

                var nextEventInfo = handleResult.NextEventInfoModel;
                if (nextEventInfo != null)
                {
                    if (TryGetIndexByItemIndex(eventInfoModels, nextEventInfo.ItemIndex, out var sourceIndex))
                    {
                        i = sourceIndex - 1;
                    }
                }
            }
        }

        private IntPtr GetWindowHandle(Process process, out ApplicationTemplate template)
        {
            template = TemplateContainer<ApplicationTemplate>.Find(process.ProcessName);

            if (string.IsNullOrEmpty(template.HandleName))
            {
                return process.MainWindowHandle;
            }

            var item = NativeHelper.GetChildHandles(process.MainWindowHandle)
                .FirstOrDefault(r => r.Item1.Equals(template.HandleName));

            return item != null ? item.Item2 : process.MainWindowHandle;
        }

        private EventResult HandleEvent(
            Process process,
            EventInfoModel model,
            SearchResult result,
            CancellationToken cancellationToken)
        {
            var windowHandle = GetWindowHandle(process, out var template);
            var matchedLocation = result.Location;

            LogHelper.Debug($"Similarity : {result.Similarity} % max Loc : X : {matchedLocation.X} Y: {matchedLocation.Y}");

            if (model.SubEventItems.Count > 0)
            {
                ProcessSubEventTriggers(process, model, cancellationToken);
            }
            else if (model.SameImageDrag == true)
            {
                Bitmap searchBmp = _currentSourceBmp;
                IntRect roiRect = default;
                bool useRoi = result.UseRoi;

                if (useRoi && _currentSourceBmp != null)
                {
                    searchBmp = OpenCVHelper.CropImage(_currentSourceBmp, result.RoiRect);
                    roiRect = result.RoiRect;
                }

                for (int i = 0; i < model.MaxDragCount; ++i)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    var locations = OpenCVHelper.MultipleSearch(
                        searchBmp, model.Image, _config.Similarity, 2, false);

                    if (locations.Count > 1)
                    {
                        var startPoint = new Point2D(
                            locations[0].X + model.Image.Width / 2 + (useRoi ? roiRect.Left : 0),
                            locations[0].Y + model.Image.Height / 2 + (useRoi ? roiRect.Top : 0));
                        startPoint.X += _eventProcessorHandler.GetRandomValue(0, model.Image.Width / 2);
                        startPoint.Y += _eventProcessorHandler.GetRandomValue(0, model.Image.Height / 2);

                        var endPoint = new Point2D(
                            locations[1].X + model.Image.Width / 2 + (useRoi ? roiRect.Left : 0),
                            locations[1].Y + model.Image.Width / 2 + (useRoi ? roiRect.Top : 0));
                        endPoint.X += _eventProcessorHandler.GetRandomValue(0, model.Image.Width / 2);
                        endPoint.Y += _eventProcessorHandler.GetRandomValue(0, model.Image.Height / 2);

                        _eventProcessorHandler.ProcessSameImageMouseDragEvent(
                            windowHandle, startPoint, endPoint, model, _config.DragDelay);
                    }
                    else
                    {
                        break;
                    }
                }

                if (searchBmp != _currentSourceBmp)
                {
                    searchBmp?.Dispose();
                }
            }
            else
            {
                if (model.EventType == EventType.Mouse)
                {
                    _eventProcessorHandler.ProcessMouseEvent(windowHandle, model, matchedLocation, template, _config.DragDelay);
                }
                else if (model.EventType == EventType.Image)
                {
                    _eventProcessorHandler.ProcessImageEvent(windowHandle, model, matchedLocation, template);
                }
                else if (model.EventType == EventType.RelativeToImage)
                {
                    _eventProcessorHandler.ProcessRelativeToImageEvent(windowHandle, model, matchedLocation, template);
                }
                else if (model.EventType == EventType.Keyboard)
                {
                    _eventProcessorHandler.ProcessKeyboardEvent(windowHandle, model);
                }

                EventInfoModel nextModel = null;

                if (model.EventToNext > 0 && model.ItemIndex != model.EventToNext)
                {
                    nextModel = _cacheDataManager.GetEventInfoModel(model.EventToNext);
                    if (nextModel != null)
                    {
                        LogHelper.Debug($">>>>Next Move Event : CurrentIndex [ {model.ItemIndex} ] NextIndex [ {nextModel.ItemIndex} ] ");
                    }
                }

                TaskHelper.TokenCheckDelay(model.AfterDelay, cancellationToken);
                return new EventResult(true, nextModel);
            }

            return new EventResult(false, null);
        }

        private void ProcessSubEventTriggers(
            Process process,
            EventInfoModel model,
            CancellationToken cancellationToken)
        {
            for (int i = 0; i < model.RepeatInfo.Count; ++i)
            {
                if (TaskHelper.TokenCheckDelay(model.AfterDelay, cancellationToken) == false)
                    break;

                if (_screenCaptureManager.CaptureProcessWindow(process,
                    out Bitmap sourceBmp) == false)
                    break;

                _currentSourceBmp = sourceBmp;

                try
                {
                    for (int ii = 0; ii < model.SubEventItems.Count; ++ii)
                    {
                        var childModel = model.SubEventItems[ii];
                        var childResult = SearchSingleItem(sourceBmp, childModel);

                        if (model.RepeatInfo.RepeatType == RepeatType.RepeatOnChildEvent)
                        {
                            if (childResult.Similarity < _config.Similarity)
                                break;
                        }

                        if (cancellationToken.IsCancellationRequested)
                            break;

                        HandleEvent(process, childModel, childResult, cancellationToken);
                    }

                    if (model.RepeatInfo.RepeatType == RepeatType.StopOnParentImage)
                    {
                        var parentResult = SearchSingleItem(sourceBmp, model);
                        if (parentResult.Similarity >= _config.Similarity)
                            break;
                    }
                }
                finally
                {
                    sourceBmp?.Dispose();
                    _currentSourceBmp = null;
                }
            }
        }

        private bool TryGetIndexByItemIndex(ArrayQueue<EventInfoModel> source, ulong findItemIndex, out int sourceIndex)
        {
            sourceIndex = -1;

            foreach (var item in source)
            {
                sourceIndex++;
                if (item.ItemIndex == findItemIndex)
                    return true;
            }
            return false;
        }
    }
}