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

            using (sourceBmp)
            {
                Draw(sourceBmp);

                var preResults = new ConcurrentDictionary<EventInfoModel, Tuple<int, Point2D>>();
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
                            loopState.Stop(); return;
                        }
                        preResults[model] = SearchEventItem(sourceBmp, model);
                    });
                }

                for (int i = 0; i < eventInfoModels.Count; ++i)
                {
                    var model = eventInfoModels[i];

                    Tuple<int, Point2D> pre = null;
                    preResults.TryGetValue(model, out pre);

                    var result = HandleEvent(sourceBmp, process, model, cancellationToken, pre);

                    var nextEventInfo = result.NextEventInfoModel;
                    if (nextEventInfo != null)
                    {
                        if (TryGetIndexByItemIndex(eventInfoModels, nextEventInfo.ItemIndex, out var sourceIndex))
                        {
                            i = sourceIndex - 1;
                        }
                    }
                }
            }
        }

        private Tuple<int, Point2D> SearchEventItem(Bitmap sourceBmp, EventInfoModel model)
        {
            if (model.RoiDataInfo.IsExists())
            {
                var rect = _screenCaptureManager.AdjustRectForDPI(
                    model.RoiDataInfo.RoiRect, model.RoiDataInfo.MonitorInfo);

                int imgW = sourceBmp.Width;
                int imgH = sourceBmp.Height;

                if (rect.Left < 0 || rect.Right > imgW || rect.Top < 0 || rect.Bottom > imgH)
                {
                    rect.Left = 0; rect.Right = imgW; rect.Top = 0; rect.Bottom = imgH;
                }
                else
                {
                    rect.Left = Math.Max(0, Math.Min(rect.Left, imgW - 1));
                    rect.Right = Math.Max(rect.Left + 1, Math.Min(rect.Right, imgW - 1));
                    rect.Top = Math.Max(0, Math.Min(rect.Top, imgH - 1));
                    rect.Bottom = Math.Max(rect.Top + 1, Math.Min(rect.Bottom, imgH - 1));
                }

                Bitmap roiBmp = null;
                try
                {
                    roiBmp = OpenCVHelper.CropImage(sourceBmp, rect);
                }
                catch (Exception ex)
                {
                    LogHelper.Error(ex);
                    return Tuple.Create(0, new Point2D());
                }

                if (roiBmp != null)
                {
                    using (roiBmp)
                    {
                        var r = OpenCVHelper.SearchOnly(roiBmp, model.Image);
                        return Tuple.Create(r.Item1, new Point2D(r.Item2.X + rect.Left, r.Item2.Y + rect.Top));
                    }
                }
                return Tuple.Create(0, new Point2D());
            }
            else
            {
                return OpenCVHelper.SearchOnly(sourceBmp, model.Image);
            }
        }

        private EventResult HandleEvent(
            Bitmap capturedImage,
            Process process,
            EventInfoModel eventInfoModel,
            CancellationToken cancellationToken,
            Tuple<int, Point2D> preSearch = null)
        {
            int similarity;
            Point2D matchedLocation;

            if (preSearch != null)
            {
                similarity = preSearch.Item1;
                matchedLocation = preSearch.Item2;
            }
            else
            {
                var copyBitmap = (Bitmap)capturedImage.Clone();
                var matchResult = CalculateSimilarityAndLocation(eventInfoModel.Image, copyBitmap, eventInfoModel);
                similarity = matchResult.Item1;
                matchedLocation = matchResult.Item2;
                Draw(copyBitmap);
                copyBitmap.Dispose();
            }

            LogHelper.Debug($"Similarity : {similarity} % max Loc : X : {matchedLocation.X} Y: {matchedLocation.Y}");

            if (similarity < _config.Similarity)
            {
                TaskHelper.TokenCheckDelay(_config.ItemDelay, cancellationToken);
                return new EventResult(false, null);
            }

            if (eventInfoModel.SubEventItems.Count > 0)
            {
                ProcessSubEventTriggers(process, eventInfoModel, cancellationToken);
            }
            else if (eventInfoModel.SameImageDrag == true)
            {
                for (int i = 0; i < eventInfoModel.MaxDragCount; ++i)
                {
                    var locations = OpenCVHelper.MultipleSearch(
                        capturedImage, eventInfoModel.Image, _config.Similarity, 2, false);

                    if (locations.Count > 1)
                    {
                        var startPoint = new Point2D(
                            locations[0].X + eventInfoModel.Image.Width / 2,
                            locations[0].Y + eventInfoModel.Image.Height / 2);

                        startPoint.X += _eventProcessorHandler.GetRandomValue(0, eventInfoModel.Image.Width / 2);
                        startPoint.Y += _eventProcessorHandler.GetRandomValue(0, eventInfoModel.Image.Height / 2);

                        var endPoint = new Point2D(
                            locations[1].X + eventInfoModel.Image.Width / 2,
                            locations[1].Y + eventInfoModel.Image.Width / 2);

                        endPoint.X += _eventProcessorHandler.GetRandomValue(0, eventInfoModel.Image.Width / 2);
                        endPoint.Y += _eventProcessorHandler.GetRandomValue(0, eventInfoModel.Image.Height / 2);

                        _eventProcessorHandler.ProcessSameImageMouseDragEvent(
                            process.MainWindowHandle, startPoint, endPoint, eventInfoModel, _config.DragDelay);
                    }
                    else
                    {
                        break;
                    }
                }
            }
            else
            {
                var windowHandle = IntPtr.Zero;
                var template = TemplateContainer<ApplicationTemplate>.Find(process.ProcessName);

                if (string.IsNullOrEmpty(template.HandleName))
                {
                    windowHandle = process.MainWindowHandle;
                }
                else
                {
                    var item = NativeHelper.GetChildHandles(process.MainWindowHandle)
                        .FirstOrDefault(r => r.Item1.Equals(template.HandleName));
                    windowHandle = item != null ? item.Item2 : process.MainWindowHandle;
                }

                if (eventInfoModel.EventType == EventType.Mouse)
                {
                    _eventProcessorHandler.ProcessMouseEvent(windowHandle, eventInfoModel, matchedLocation, template, _config.DragDelay);
                }
                else if (eventInfoModel.EventType == EventType.Image)
                {
                    _eventProcessorHandler.ProcessImageEvent(windowHandle, eventInfoModel, matchedLocation, template);
                }
                else if (eventInfoModel.EventType == EventType.RelativeToImage)
                {
                    _eventProcessorHandler.ProcessRelativeToImageEvent(windowHandle, eventInfoModel, matchedLocation, template);
                }
                else if (eventInfoModel.EventType == EventType.Keyboard)
                {
                    _eventProcessorHandler.ProcessKeyboardEvent(windowHandle, eventInfoModel);
                }

                EventInfoModel nextModel = null;

                if (eventInfoModel.EventToNext > 0 && eventInfoModel.ItemIndex != eventInfoModel.EventToNext)
                {
                    nextModel = _cacheDataManager.GetEventInfoModel(eventInfoModel.EventToNext);

                    if (nextModel != null)
                    {
                        LogHelper.Debug($">>>>Next Move Event : CurrentIndex [ {eventInfoModel.ItemIndex} ] NextIndex [ {nextModel.ItemIndex} ] ");
                    }
                }
                TaskHelper.TokenCheckDelay(eventInfoModel.AfterDelay, cancellationToken);

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
                {
                    break;
                }

                if (_screenCaptureManager.CaptureProcessWindow(process,
                    out Bitmap sourceBmp) == false)
                {
                    break;
                }

                using (sourceBmp)
                {
                    for (int ii = 0; ii < model.SubEventItems.Count; ++ii)
                    {
                        var childModel = model.SubEventItems[ii];
                        var preResult = SearchEventItem(sourceBmp, childModel);

                        if (model.RepeatInfo.RepeatType == RepeatType.RepeatOnChildEvent)
                        {
                            if (preResult.Item1 < _config.Similarity)
                                break;
                        }

                        if (cancellationToken.IsCancellationRequested == true)
                            break;

                        var handleResult = HandleEvent(sourceBmp, process, childModel, cancellationToken, preResult);

                        if (model.RepeatInfo.RepeatType == RepeatType.RepeatOnChildEvent)
                        {
                            if (handleResult.IsSuccess == false)
                                break;
                        }
                    }

                    if (model.RepeatInfo.RepeatType == RepeatType.StopOnParentImage)
                    {
                        var stopResult = SearchEventItem(sourceBmp, model);
                        if (stopResult.Item1 >= _config.Similarity)
                            break;
                    }
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