﻿/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 * 
 * Licensed under the Apache License, Version 2.0 (the "License"); 
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *
*/
/**********************************************************
* USING NAMESPACES
**********************************************************/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Packets;

namespace QuantConnect.Lean.Engine.Results
{
    /// <summary>
    /// Live trading result handler implementation passes the messages to the QC live trading interface.
    /// </summary>
    /// <remarks>Live trading result handler is quite busy. It sends constant price updates, equity updates and order/holdings updates.</remarks>
    public class LiveTradingResultHandler : IResultHandler
    {
        /******************************************************** 
        * CLASS VARIABLES
        *********************************************************/
        // Required properties for the cloud app.
        private string _compileId = "";
        private string _deployId = "";
        private bool _isActive = false;
        private ConcurrentDictionary<string, Chart> _charts;
        private ConcurrentQueue<Packet> _messages;
        private IAlgorithm _algorithm;
        private bool _exitTriggered = false;
        private DateTime _startTime = new DateTime();
        private LiveNodePacket _job;
        private Dictionary<string, string> _runtimeStatistics = new Dictionary<string, string>();

        //Sampling Periods:
        private TimeSpan _resamplePeriod = TimeSpan.FromMinutes(1);
        private TimeSpan _notificationPeriod = TimeSpan.FromSeconds(2);

        //Update loop:
        private DateTime _nextUpdate = new DateTime();
        private DateTime _nextEquityUpdate = new DateTime();
        private DateTime _nextChartsUpdate = new DateTime();
        private DateTime _nextLogStoreUpdate = new DateTime();
        private DateTime _lastUpdate = new DateTime();
        private int _lastOrderId = -1;
        private object _chartLock = new Object();
        private object _runtimeLock = new Object();

        //Log Message Store:
        private object _logStoreLock = new object();
        private Dictionary<DateTime, List<string>> _logStore = new Dictionary<DateTime, List<string>>();

        /******************************************************** 
        * CLASS PROPERTIES
        *********************************************************/
        /// <summary>
        /// Live packet messaging queue. Queue the messages here and send when the result queue is ready.
        /// </summary>
        public ConcurrentQueue<Packet> Messages
        {
            get
            {
                return _messages;
            }
            set
            {
                _messages = value;
            }
        }

        /// <summary>
        /// Storage for the price and equity charts of the live results.
        /// </summary>
        /// <remarks>
        ///     Potential memory leak when the algorithm has been running for a long time. Infinitely storing the results isn't wise.
        ///     The results should be stored to disk daily, and then the caches reset.
        /// </remarks>
        public ConcurrentDictionary<string, Chart> Charts
        {
            get
            {
                return _charts;
            }
            set
            {
                _charts = value;
            }
        }

        /// <summary>
        /// Boolean flag indicating the thread is still active.
        /// </summary>
        public bool IsActive
        {
            get
            {
                return _isActive;
            }
        }

        /// <summary>
        /// Equity resampling period for the charting.
        /// </summary>
        /// <remarks>Live trading can resample at much higher frequencies (every 1-2 seconds)</remarks>
        public TimeSpan ResamplePeriod
        {
            get
            {
                return _resamplePeriod;
            }
        }

        /// <summary>
        /// Notification periods set how frequently we push updates to the browser.
        /// </summary>
        /// <remarks>Live trading resamples - sends updates at high frequencies(every 1-2 seconds)</remarks>
        public TimeSpan NotificationPeriod
        {
            get
            {
                return _notificationPeriod;
            }
        }

        /******************************************************** 
        * CONSTRUCTOR
        *********************************************************/
        /// <summary>
        /// Initialize the live trading result handler
        /// </summary>
        /// <param name="job">Live trading job</param>
        public LiveTradingResultHandler(LiveNodePacket job)
        {
            _job = job;
            _deployId = job.DeployId;
            _compileId = job.CompileId;
            _charts = new ConcurrentDictionary<string, Chart>();
            _messages = new ConcurrentQueue<Packet>();
            _isActive = true;
            _runtimeStatistics = new Dictionary<string, string>();

            _resamplePeriod = TimeSpan.FromSeconds(1);
            _notificationPeriod = TimeSpan.FromSeconds(1);
            _startTime = DateTime.Now;

            //Store log and debug messages sorted by time.
            _logStore = new Dictionary<DateTime, List<string>>();
        }


        /******************************************************** 
        * CLASS METHODS
        *********************************************************/
        /// <summary>
        /// Live trading result handler thread.
        /// </summary>
        public void Run()
        {
            // -> 1. Run Primary Sender Loop: Continually process messages from queue as soon as they arrive.
            while (!(_exitTriggered && Messages.Count == 0))
            {
                try
                {

                    //While there's no work to do, go back to the algorithm:
                    if (Messages.Count == 0)
                    {
                        Thread.Sleep(10);
                    }
                    else
                    {
                        //1. Process Simple Messages in Queue
                        Packet packet;
                        if (Messages.TryDequeue(out packet))
                        {
                            switch (packet.Type)
                            {
                                //New Debug Message:
                                case PacketType.Debug:
                                    var debug = packet as DebugPacket;
                                    Log.Trace("LiveTradingResultHandlerRun(): Debug Packet: " + debug.Message);
                                    Engine.Notify.DebugMessage(debug.Message, debug.ProjectId, _deployId, _compileId);
                                    break;

                                case PacketType.RuntimeError:
                                    var runtimeError = packet as RuntimeErrorPacket;
                                    Engine.Notify.RuntimeError(_deployId, runtimeError.Message);
                                    break;

                                //Send log messages to the browser as well for live trading:
                                case PacketType.Log:
                                    var log = packet as LogPacket;
                                    Log.Trace("LiveTradingResultHandler.Run(): Log Packet: " + log.Message);
                                    Engine.Notify.LogMessage(_deployId, log.Message);
                                    break;

                                //Send log messages to the browser as well for live trading:
                                case PacketType.SecurityTypes:
                                    var securityPacket = packet as SecurityTypesPacket;
                                    Log.Trace("LiveTradingResultHandler.Run(): Security Types Packet: " + securityPacket.TypesCSV);
                                    Engine.Notify.SecurityTypes(securityPacket);
                                    break;

                                //Status Update
                                case PacketType.AlgorithmStatus:
                                    var statusPacket = packet as AlgorithmStatusPacket;
                                    Log.Trace("LiveTradingResultHandler.Run(): Algorithm Status Packet:" + statusPacket.Status + " " + statusPacket.AlgorithmId);
                                    Engine.Notify.AlgorithmStatus(statusPacket.AlgorithmId, statusPacket.Status, statusPacket.Message);
                                    break;

                                default:
                                    Log.Error("LiveTradingResultHandler.Run(): Case Unhandled: " + packet.Type.ToString());
                                    break;
                            }
                        }
                    }

                    //2. Update the packet scanner:
                    Update();
                }
                catch (Exception err)
                {
                    //Error never hit but just in case.
                    Log.Error("LiveTradingResultHandler.Run(): " + err.Message);
                }
            } // While !End.

            Log.Trace("LiveTradingResultHandler.Run(): Ending Thread...");
            _isActive = false;
        } // End Run();


        /// <summary>
        /// Every so often send an update to the browser with the current state of the algorithm.
        /// </summary>
        public void Update()
        {
            //Initialize:
            var deltaOrders = new Dictionary<int, Order>();

            //Error checks if the algorithm & threads have not loaded yet, or are closing down.
            if (_algorithm == null || _algorithm.Transactions == null || _algorithm.Transactions.Orders == null)
            {
                return;
            }

            try
            {
                if (DateTime.Now > _nextUpdate)
                {
                    //Extract the orders created since last update
                    deltaOrders = (from order in _algorithm.Transactions.Orders
                                   where order.Value.Id > _lastOrderId
                                   select order).ToDictionary(t => t.Key, t => t.Value);

                    //Reset loop variables:
                    _lastOrderId = (from order in deltaOrders.Values select order.Id).Max();
                    _lastUpdate = AlgorithmManager.Frontier;
                    
                    //Limit length of orders we pass back dynamically to avoid flooding.
                    //if (deltaOrders.Count > 50) deltaOrders.Clear();

                    //Create and send back the changes in chart since the algorithm started.
                    var deltaCharts = new Dictionary<string, Chart>();
                    lock (_chartLock)
                    {
                        //Get the updates since the last chart
                        foreach (var chart in Charts.Values)
                        {
                            if (chart.Name != "Strategy Equity")
                            {
                                deltaCharts.Add(chart.Name, chart.GetUpdates());
                            }

                            if ((chart.Name == "Strategy Equity") && (DateTime.Now > _nextEquityUpdate))
                            {
                                _nextEquityUpdate = DateTime.Now.AddSeconds(60);
                                deltaCharts.Add(chart.Name, chart.GetUpdates());
                            }
                        }
                    }

                    var holdings = new Dictionary<string, Holding>();
                    foreach (var holding in _algorithm.Portfolio.Values)
                    {
                        holdings.Add(holding.Symbol, new Holding(holding));
                    }

                    //Profit loss changes.
                    var deltaProfitLoss = new Dictionary<DateTime, decimal>();
                    var deltaStatistics = new Dictionary<string, string>();

                    //Get the banner statistics, summary information on the performance for the headers.
                    var runtimeStatistics = new Dictionary<string, string>();

                    //Add the algorithm statistics first.
                    lock (_runtimeStatistics)
                    {
                        foreach (var pair in _runtimeStatistics)
                        {
                            runtimeStatistics.Add(pair.Key, pair.Value);
                        }
                    }

                    //Add other fixed parameters.
                    runtimeStatistics.Add("Unrealized:", _algorithm.Portfolio.TotalUnrealizedProfit.ToString("C"));
                    runtimeStatistics.Add("Fees:", _algorithm.Portfolio.TotalFees.ToString("C"));
                    runtimeStatistics.Add("Net Profit:", _algorithm.Portfolio.TotalProfit.ToString("C"));
                    runtimeStatistics.Add("Return:", (_algorithm.Portfolio.TotalPortfolioValue / Engine.SetupHandler.StartingCapital).ToString("P"));
                    runtimeStatistics.Add("Holdings:", _algorithm.Portfolio.TotalHoldingsValue.ToString("C"));
                    runtimeStatistics.Add("Volume:", _algorithm.Portfolio.TotalSaleVolume.ToString("C"));

                    //Create the simultor result packet.
                    var packet = new LiveResultPacket(_job, new LiveResult(deltaCharts, deltaOrders, deltaProfitLoss, holdings, deltaStatistics, runtimeStatistics));

                    //Send to the user terminal directly.
                    Engine.Notify.LiveTradingResult(packet);

                    //Send full packet to storage.
                    if (DateTime.Now > _nextChartsUpdate)
                    {
                        _nextChartsUpdate = DateTime.Now.AddMinutes(1);
                        lock (_chartLock)
                        {
                            var chartComplete = new Dictionary<string, Chart>(Charts);
                            var ordersComplete = new Dictionary<int, Order>(_algorithm.Transactions.Orders);
                            var complete = new LiveResultPacket(_job, new LiveResult(chartComplete, ordersComplete, _algorithm.Transactions.TransactionRecord, holdings, deltaStatistics, runtimeStatistics));
                            StoreResult(complete, true);
                        }
                    }

                    // Upload the logs every 1-2 minutes; this can be a heavy operation depending on amount of live logging and should probably be done asynchronously.
                    if (DateTime.Now > _nextLogStoreUpdate)
                    {
                        var date = DateTime.Now.Date;
                        _nextLogStoreUpdate = DateTime.Now.AddMinutes(1);

                        lock (_logStoreLock)
                        {
                            //Make sure we have logs for this day:
                            if (_logStore.ContainsKey(date)) StoreLog(date, _logStore[date]);

                            //Clear all log store dates not today (save RAM with long running algorithm)
                            var keys = _logStore.Keys.ToList();
                            foreach (var key in keys)
                            {
                                if (key.Date != DateTime.Now.Date) _logStore.Remove(key);
                            }
                        }
                    }

                    //Set the new update time after we've finished processing. 
                    // The processing can takes time depending on how large the packets are.
                    _nextUpdate = DateTime.Now.AddSeconds(2);

                } // End Update Charts:                
            }
            catch (Exception err)
            {
                Log.Error("LiveTradingResultHandler().ProcessSeriesUpdate(): " + err.Message, true);
            }
        }


        /// <summary>
        /// Send a live trading debug message to the live console.
        /// </summary>
        /// <param name="message">Message we'd like shown in console.</param>
        /// <remarks>When there are already 500 messages in the queue it stops adding new messages.</remarks>
        public void DebugMessage(string message)
        {
            if (Messages.Count > 500) return; //if too many in the queue already skip the logging.
            Messages.Enqueue(new DebugPacket(_job.ProjectId, _deployId, _compileId, message));
            AddToLogStore(message);
        }

        /// <summary>
        /// Log string messages and send them to the console.
        /// </summary>
        /// <param name="message">String message wed like logged.</param>
        /// <remarks>When there are already 500 messages in the queue it stops adding new messages.</remarks>
        public void LogMessage(string message)
        {
            //Send the logging messages out immediately for live trading:
            if (Messages.Count > 500) return;
            Messages.Enqueue(new LogPacket(_deployId, message));
            AddToLogStore(message);
        }

        /// <summary>
        /// Save an algorithm message to the log store. Uses a different timestamped method of adding messaging to interweve debug and logging messages.
        /// </summary>
        /// <param name="message">String message to send to browser.</param>
        private void AddToLogStore(string message)
        {
            var date = DateTime.Now.Date;
            lock (_logStoreLock)
            {
                if (!_logStore.ContainsKey(date)) _logStore.Add(date, new List<string>());
                _logStore[date].Add(DateTime.Now.ToString("u") + " " + message);
            }
        }

        /// <summary>
        /// Send an error message back to the browser console and highlight it read.
        /// </summary>
        /// <param name="message">Message we'd like shown in console.</param>
        /// <param name="stacktrace">Stacktrace to show in the console.</param>
        public void ErrorMessage(string message, string stacktrace = "")
        {
            if (Messages.Count > 500) return;
            Messages.Enqueue(new RuntimeErrorPacket(_deployId, message, stacktrace));
        }

        /// <summary>
        /// Send a list of secutity types that the algorithm trades to the browser to show the market clock - is this market open or closed!
        /// </summary>
        /// <param name="types">List of security types</param>
        public void SecurityType(List<SecurityType> types)
        {
            var packet = new SecurityTypesPacket {Types = types};
            Messages.Enqueue(packet);
        }

        /// <summary>
        /// Send a runtime error back to the users browser and highlight it red.
        /// </summary>
        /// <param name="message">Runtime error message</param>
        /// <param name="stacktrace">Associated error stack trace.</param>
        public void RuntimeError(string message, string stacktrace = "")
        {
            PurgeQueue();
            Messages.Enqueue(new RuntimeErrorPacket(_deployId, message, stacktrace));
        }

        /// <summary>
        /// Add a sample to the chart specified by the chartName, and seriesName.
        /// </summary>
        /// <param name="chartName">String chart name to place the sample.</param>
        /// <param name="chartType">Type of chart we should create if it doesn't already exist.</param>
        /// <param name="seriesName">Series name for the chart.</param>
        /// <param name="seriesType">Series type for the chart.</param>
        /// <param name="time">Time for the sample</param>
        /// <param name="value">Value for the chart sample.</param>
        /// <remarks>Sample can be used to create new charts or sample equity - daily performance.</remarks>
        public void Sample(string chartName, ChartType chartType, string seriesName, SeriesType seriesType, DateTime time, decimal value)
        {
            lock (_chartLock)
            {
                //Add a copy locally:
                if (!Charts.ContainsKey(chartName))
                {
                    Charts.AddOrUpdate<string, Chart>(chartName, new Chart(chartName, chartType));
                }

                //Add the sample to our chart:
                if (!Charts[chartName].Series.ContainsKey(seriesName))
                {
                    Charts[chartName].Series.Add(seriesName, new Series(seriesName, seriesType));
                }

                //Add our value:
                Charts[chartName].Series[seriesName].Values.Add(new ChartPoint(time, value));
            }
        }

        /// <summary>
        /// Wrapper methond on sample to create the equity chart.
        /// </summary>
        /// <param name="time">Time of the sample.</param>
        /// <param name="value">Equity value at this moment in time.</param>
        /// <seealso cref="Sample(string,ChartType,string,SeriesType,DateTime,decimal)"/>
        public void SampleEquity(DateTime time, decimal value)
        {
            if (value > 0)
            {
                Log.Debug("LiveTradingResultHandler.SampleEquity(): " + time.ToShortTimeString() + " >" + value);
                Sample("Strategy Equity", ChartType.Stacked, "Equity", SeriesType.Candle, time, value);
            }
        }


        /// <summary>
        /// Sample the asset prices to generate plots.
        /// </summary>
        /// <param name="symbol">Symbol we're sampling.</param>
        /// <param name="time">Time of sample</param>
        /// <param name="value">Value of the asset price</param>
        /// <seealso cref="Sample(string,ChartType,string,SeriesType,DateTime,decimal)"/>
        public void SampleAssetPrices(string symbol, DateTime time, decimal value)
        {
            if (_algorithm.Securities.ContainsKey(symbol) && value > 0)
            {
                if (DateTime.Now.TimeOfDay > _algorithm.Securities[symbol].Exchange.MarketOpen 
                 && DateTime.Now.TimeOfDay < _algorithm.Securities[symbol].Exchange.MarketClose)
                {
                    Sample("Stockplot: " + symbol, ChartType.Overlay, "Stockplot: " + symbol, SeriesType.Line, time, value);
                }
            }
        }

        /// <summary>
        /// Sample the current daily performance directly with a time-value pair.
        /// </summary>
        /// <param name="time">Current backtest date.</param>
        /// <param name="value">Current daily performance value.</param>
        /// <seealso cref="Sample(string,ChartType,string,SeriesType,DateTime,decimal)"/>
        public void SamplePerformance(DateTime time, decimal value)
        {

            Log.Debug("LiveTradingResultHandler.SamplePerformance(): " + time.ToShortTimeString() + " >" + value);
            Sample("Daily Performance", ChartType.Overlay, "Performance", SeriesType.Line, time, value);
        }

        /// <summary>
        /// Add a range of samples from the users algorithms to the end of our current list.
        /// </summary>
        /// <param name="updates">Chart updates since the last request.</param>
        /// <seealso cref="Sample(string,ChartType,string,SeriesType,DateTime,decimal)"/>
        public void SampleRange(List<Chart> updates)
        {
            lock (_chartLock)
            {
                foreach (var update in updates)
                {
                    //Create the chart if it doesn't exist already:
                    if (!Charts.ContainsKey(update.Name))
                    {
                        Charts.AddOrUpdate<string, Chart>(update.Name, new Chart(update.Name, update.ChartType));
                    }

                    //Add these samples to this chart.
                    foreach (var series in update.Series.Values)
                    {
                        //If we don't already have this record, its the first packet
                        if (!Charts[update.Name].Series.ContainsKey(series.Name))
                        {
                            Charts[update.Name].Series.Add(series.Name, new Series(series.Name, series.SeriesType));
                        }

                        //We already have this record, so just the new samples to the end:
                        Charts[update.Name].Series[series.Name].Values.AddRange(series.Values);
                    }
                }
            }
        }

        /// <summary>
        /// Set the algorithm of the result handler after its been initialized.
        /// </summary>
        /// <param name="algorithm">Algorithm object matching IAlgorithm interface</param>
        public void SetAlgorithm(IAlgorithm algorithm)
        {
            _algorithm = algorithm;

            var types = new List<SecurityType>();
            foreach (var security in _algorithm.Securities.Values)
            {
                if (!types.Contains(security.Type)) types.Add(security.Type);
            }
            SecurityType(types);
        }


        /// <summary>
        /// Send a algorithm status update to the user of the algorithms running state.
        /// </summary>
        /// <param name="algorithmId">String Id of the algorithm.</param>
        /// <param name="status">Status enum of the algorithm.</param>
        /// <param name="message">Optional string message describing reason for status change.</param>
        public void SendStatusUpdate(string algorithmId, AlgorithmStatus status, string message = "")
        {
            Log.Trace("LiveTradingResultHandler.SendStatusUpdate(): " + status);
            var packet = new AlgorithmStatusPacket(algorithmId, status, message);
            Messages.Enqueue(packet);
        }


        /// <summary>
        /// Set a dynamic runtime statistic to show in the (live) algorithm header
        /// </summary>
        /// <param name="key">Runtime headline statistic name</param>
        /// <param name="value">Runtime headline statistic value</param>
        public void RuntimeStatistic(string key, string value)
        {
            lock (_runtimeLock)
            {
                if (!_runtimeStatistics.ContainsKey(key))
                {
                    _runtimeStatistics.Add(key, value);
                }
                _runtimeStatistics[key] = value;
            }
        }

        /// <summary>
        /// Send a final analysis result back to the IDE.
        /// </summary>
        /// <param name="job">Lean AlgorithmJob task</param>
        /// <param name="orders">Collection of orders from the algorithm</param>
        /// <param name="profitLoss">Collection of time-profit values for the algorithm</param>
        /// <param name="holdings">Current holdings state for the algorithm</param>
        /// <param name="statistics">Statistics information for the algorithm (empty if not finished)</param>
        /// <param name="runtime">Runtime statistics banner information</param>
        public void SendFinalResult(AlgorithmNodePacket job, Dictionary<int, Order> orders, Dictionary<DateTime, decimal> profitLoss, Dictionary<string, Holding> holdings, Dictionary<string, string> statistics, Dictionary<string, string> runtime)
        {
            try
            {
                //Convert local dictionary:
                var charts = new Dictionary<string, Chart>(Charts);

                //Create a packet:
                LiveResultPacket result = new LiveResultPacket((LiveNodePacket)job, new LiveResult(charts, orders, profitLoss, holdings, statistics, runtime));

                //Save the processing time:
                result.ProcessingTime = (DateTime.Now - _startTime).TotalSeconds;

                //Store to S3:
                StoreResult(result, false);

                //Truncate packet to fit within 32kb:
                result.Results = new LiveResult();

                //Send the truncated packet:
                Engine.Notify.LiveTradingResult(result);
            }
            catch (Exception err)
            {
                Log.Error("Algorithm.Worker.SendResult(): " + err.Message);
            }
        }


        /// <summary>
        /// Process the log list and save it to storage.
        /// </summary>
        /// <param name="date">Today's date for this log</param>
        /// <param name="logs">Log list</param>
        public void StoreLog(DateTime date, List<string> logs)
        {
            try
            {
                var key = "live/" + _job.UserId + "/" + _job.ProjectId + "/" + _job.DeployId + "-" + DateTime.Now.ToString("yyyy-MM-dd") + "-log.txt";
                var serialized = "";
                foreach (var log in logs)
                {
                    serialized += log + "\r\n";
                }

                //For live trading we're making assumption its a long running task and safe to async save large files.
                Engine.Controls.Store(serialized, key, StoragePermissions.Authenticated, true);
            }
            catch (Exception err)
            {
                Log.Error("LiveTradingResultHandler.StoreLog(): " + err.Message);
            }
        }

        /// <summary>
        /// Save the snapshot of the total results to storage.
        /// </summary>
        /// <param name="packet">Packet to store.</param>
        /// <param name="async">Store the packet asyncronously to speed up the thread.</param>
        /// <remarks>
        ///     Async creates crashes in Mono 3.10 if the thread disappears before the upload is complete so it is disabled for now.
        ///     For live trading we're making assumption its a long running task and safe to async save large files.
        /// </remarks>
        public void StoreResult(Packet packet, bool async = true)
        {
            //Initialize:
            var serialized = "";
            var key = "";

            try
            {
                lock (_chartLock)
                {
                    //1. Make sure this is the right type of packet:
                    if (packet.Type != PacketType.LiveResult) return;

                    //2. Port to packet format:
                    var live = packet as LiveResultPacket;

                    if (live != null)
                    {
                        //3. Get the S3 Storage Location:
                        key = "live/" + _job.UserId + "/" + _job.ProjectId + "/" + _job.DeployId + "-" +  DateTime.Now.ToString("yyyy-MM-dd") + ".json";

                        //4. Serialize to JSON:
                        serialized = JsonConvert.SerializeObject(live.Results);
                    }
                    else
                    {
                        Log.Error("LiveResultHandler.StoreResult(): Result Null.");
                    }
                }

                //Upload Results Portion
                Engine.Controls.Store(serialized, key, StoragePermissions.Authenticated, async);
            }
            catch (Exception err)
            {
                Log.Error("LiveResultHandler.StoreResult(): " + err.Message);
            }
        }

        /// <summary>
        /// New order event for the algorithm backtest: send event to browser.
        /// </summary>
        /// <param name="newEvent">New event details</param>
        public void OrderEvent(OrderEvent newEvent)
        {
            Log.Trace("LiveConsoleResultHandler.OrderEvent(): id:" + newEvent.OrderId + " >> Status:" + newEvent.Status.ToString() + " >> Fill Price: " + newEvent.FillPrice.ToString("C") + " >> Fill Quantity: " + newEvent.FillQuantity);
            Messages.Enqueue(new OrderEventPacket(_deployId, newEvent));
        }

        /// <summary>
        /// Terminate the result thread and apply any required exit proceedures.
        /// </summary>
        public void Exit()
        {
            _exitTriggered = true;
        }

        /// <summary>
        /// Purge/clear any outstanding messages in message queue.
        /// </summary>
        public void PurgeQueue()
        {
            Messages.Clear();
        }
    } // End Result Handler Thread:

} // End Namespace
