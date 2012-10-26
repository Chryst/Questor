﻿// ------------------------------------------------------------------------------
//   <copyright from='2010' to='2015' company='THEHACKERWITHIN.COM'>
//     Copyright (c) TheHackerWithin.COM. All Rights Reserved.
//
//     Please look in the accompanying license.htm file for the license that
//     applies to this source code. (a copy can also be found at:
//     http://www.thehackerwithin.com/license.htm)
//   </copyright>
// -------------------------------------------------------------------------------

namespace Questor.Modules.Actions
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using DirectEve;
    using global::Questor.Modules.Caching;
    using global::Questor.Modules.Logging;
    using global::Questor.Modules.Lookup;
    using global::Questor.Modules.States;

    public class UnloadLoot
    {
        public const int StationContainer = 17366;

        private static DateTime _nextUnloadAction = DateTime.Now;
        private static DateTime _lastUnloadAction = DateTime.MinValue;
        private static int _lootToMoveWillStillNotFitCount;
        private static DateTime _lastPulse;
        private static bool AmmoIsBeingMoved;
        private static bool LootIsBeingMoved;
        private static bool AllLootWillFit;
        private static List<ItemCache> ItemsToMove;
        private static IEnumerable<DirectItem> ammoToMove;
        private static IEnumerable<DirectItem> scriptsToMove;
        private static IEnumerable<DirectItem> commonMissionCompletionItemsToMove;

        //public UnloadLoot()
        //{
        //    ItemsToMove = new List<ItemCache>();
        //}

        //public double LootValue { get; set; }

        public void ProcessState()
        {
            // Only pulse state changes every 1.5s
            if (DateTime.Now.Subtract(_lastPulse).TotalMilliseconds < Time.Instance.QuestorPulse_milliseconds) //default: 1500ms
                return;
            _lastPulse = DateTime.Now;

            if (!Cache.Instance.InStation)
                return;

            if (Cache.Instance.InSpace)
                return;

            if (DateTime.Now < Cache.Instance.LastInSpace.AddSeconds(20)) // we wait 20 seconds after we last thought we were in space before trying to do anything in station
                return;

            switch (_States.CurrentUnloadLootState)
            {
                case UnloadLootState.Idle:
                case UnloadLootState.Done:
                    break;

                case UnloadLootState.Begin:
                    if (DateTime.Now < _nextUnloadAction)
                    {
                        if(Settings.Instance.DebugUnloadLoot) Logging.Log("Unloadloot", "will Continue in [ " + Math.Round(_nextUnloadAction.Subtract(DateTime.Now).TotalSeconds, 0) + " ] sec", Logging.White);
                        break;
                    }
                    AmmoIsBeingMoved = false;
                    LootIsBeingMoved = false;
                    _lastUnloadAction = DateTime.Now.AddSeconds(-10);
                    _States.CurrentUnloadLootState = UnloadLootState.MoveAmmo;
                    break;

                case UnloadLootState.MoveLoot:
                    if (DateTime.Now < _nextUnloadAction)
                    {
                        if (Settings.Instance.DebugUnloadLoot) Logging.Log("Unloadloot", "will Continue in [ " + Math.Round(_nextUnloadAction.Subtract(DateTime.Now).TotalSeconds, 0) + " ] sec", Logging.White);
                        break;
                    }

                    if (!Cache.Instance.OpenCargoHold("UnloadLoot")) return;
                    if (!Cache.Instance.OpenLootHangar("UnloadLoot")) return;

                    if (LootIsBeingMoved && AllLootWillFit)
                    {
                        if (Cache.Instance.DirectEve.GetLockedItems().Count != 0)
                        {
                            if (DateTime.Now.Subtract(_lastUnloadAction).TotalSeconds > 120)
                            {
                                Logging.Log("UnloadLoot", "Moving Loot timed out, clearing item locks", Logging.Orange);
                                Cache.Instance.DirectEve.UnlockItems();
                                _lastUnloadAction = DateTime.Now.AddSeconds(-10);
                                _States.CurrentUnloadLootState = UnloadLootState.Begin;
                                break;
                            }

                            if (Settings.Instance.DebugUnloadLoot) Logging.Log("UnloadLootState.MoveAmmo", "Waiting for Locks to clear. GetLockedItems().Count [" + Cache.Instance.DirectEve.GetLockedItems().Count + "]", Logging.Teal);
                            return;
                        }

                        if (!Cache.Instance.CloseLootHangar("UnloadLootState.MoveAmmo")) break;
                        Logging.Log("UnloadLoot", "Loot was worth an estimated [" + Statistics.Instance.LootValue.ToString("#,##0") + "] isk in buy-orders", Logging.Teal);
                        LootIsBeingMoved = false;
                        _States.CurrentUnloadLootState = UnloadLootState.Done;
                    }

                    if (Cache.Instance.CargoHold.IsValid && Cache.Instance.CargoHold.Items.Any())
                    {
                        IEnumerable<DirectItem> lootToMove = Cache.Instance.CargoHold.Items.Where(i => Settings.Instance.Ammo.All(a => a.TypeId != i.TypeId)).ToList();
                        IEnumerable<DirectItem> somelootToMove = lootToMove;
                        foreach (DirectItem item in lootToMove)
                        {
                            if (!Cache.Instance.InvTypesById.ContainsKey(item.TypeId))
                                continue;

                            Statistics.Instance.LootValue += (int)item.AveragePrice() * Math.Max(item.Quantity, 1);
                        }

                        if (Cache.Instance.LootHangar.IsValid)
                        {
                            if (string.IsNullOrEmpty(Settings.Instance.LootHangar)) // if we do NOT have the loot hangar configured. 
                            {
                                if (Settings.Instance.DebugUnloadLoot) Logging.Log("UnloadLoot", "loothangar setting is not configured, assuming lothangar is local items hangar (and its 999 item limit)", Logging.White);
                                // Move loot to the loot hangar
                                int roominHangar = (999 - Cache.Instance.LootHangar.Items.Count);
                                if (roominHangar > lootToMove.Count())
                                {
                                    if (Settings.Instance.DebugUnloadLoot) Logging.Log("UnloadLoot", "Loothangar has plenty of room to move loot all in one go", Logging.White);
                                    Cache.Instance.LootHangar.Add(lootToMove);
                                    AllLootWillFit = true;
                                    _lootToMoveWillStillNotFitCount = 0;
                                    return;
                                }

                                AllLootWillFit = false;
                                Logging.Log("Unloadloot", "Loothangar is almost full and contains [" + Cache.Instance.LootHangar.Items.Count + "] of 999 total possible stacks", Logging.Orange);
                                if (roominHangar > 50)
                                {
                                    if (Settings.Instance.DebugUnloadLoot) Logging.Log("UnloadLoot", "Loothangar has more than 50 item slots left", Logging.White);
                                    somelootToMove = lootToMove.Where(i => Settings.Instance.Ammo.All(a => a.TypeId != i.TypeId)).ToList().GetRange(0, 49).ToList();
                                }
                                else if (roominHangar > 20)
                                {
                                    if (Settings.Instance.DebugUnloadLoot) Logging.Log("UnloadLoot", "Loothangar has more than 20 item slots left", Logging.White);
                                    somelootToMove = lootToMove.Where(i => Settings.Instance.Ammo.All(a => a.TypeId != i.TypeId)).ToList().GetRange(0, 19).ToList();
                                }

                                if (somelootToMove.Any())
                                {
                                    Logging.Log("UnloadLoot", "Moving [" + somelootToMove.Count() + "]  of [" + lootToMove.Count() + "] items into the loot hangar", Logging.White);
                                    Cache.Instance.LootHangar.Add(somelootToMove);
                                    return;
                                }

                                if (_lootToMoveWillStillNotFitCount < 7)
                                {
                                    _lootToMoveWillStillNotFitCount++;
                                    if (!Cache.Instance.StackLootHangar("Unloadloot")) return;
                                    return;
                                }

                                Logging.Log("Unloadloot", "We tried to stack the loothangar 7 times and we still could not fit all the LootToMove into the LootHangar [" + Cache.Instance.LootHangar.Items.Count + " items ]", Logging.Red);
                                _States.CurrentQuestorState = QuestorState.Error;
                                return;
                            }
                            else //we must be using the corp hangar as the loothangar
                            {
                                if (lootToMove.Any())
                                {
                                    Logging.Log("UnloadLoot", "Moving [" + lootToMove.Count() + "]  of [" + Cache.Instance.LootHangar.Items.Count + "] items into the loot hangar", Logging.White);
                                    AllLootWillFit = true;
                                    Cache.Instance.LootHangar.Add(lootToMove);
                                    return;
                                }
                            }
                        }
                    }
                    //
                    // Stack LootHangar
                    //
                    if (!Cache.Instance.StackLootHangar("UnloadLoot.MoveLoot")) return;
                    break;

                case UnloadLootState.MoveAmmo:
                    if (DateTime.Now < _nextUnloadAction)
                    {
                        Logging.Log("Unloadloot", "will Continue in [ " + Math.Round(_nextUnloadAction.Subtract(DateTime.Now).TotalSeconds, 0) + " ] sec", Logging.White);
                        break;
                    }

                    if (AmmoIsBeingMoved)
                    {
                        if (Cache.Instance.DirectEve.GetLockedItems().Count != 0)
                        {
                            if (DateTime.Now.Subtract(_lastUnloadAction).TotalSeconds > 120)
                            {
                                Logging.Log("UnloadLoot", "Moving Ammo timed out, clearing item locks", Logging.Orange);
                                Cache.Instance.DirectEve.UnlockItems();
                                _lastUnloadAction = DateTime.Now.AddSeconds(-10);
                                _States.CurrentUnloadLootState = UnloadLootState.Begin;
                                break;
                            }

                            if (Settings.Instance.DebugUnloadLoot) Logging.Log("UnloadLootState.MoveAmmo", "Waiting for Locks to clear. GetLockedItems().Count [" + Cache.Instance.DirectEve.GetLockedItems().Count + "]", Logging.Teal);
                            return;
                        }

                        if (Cache.Instance.LastStackAmmoHangar.AddSeconds(30) > DateTime.Now)
                        {
                            if (!Cache.Instance.CloseAmmoHangar("UnloadLootState.MoveAmmo")) break;
                            Logging.Log("UnloadLoot.MoveAmmo", "Done Moving Ammo", Logging.White);
                            AmmoIsBeingMoved = false;
                            _States.CurrentUnloadLootState = UnloadLootState.MoveLoot;
                        }
                    }
                    
                    if (!Cache.Instance.OpenCargoHold("UnloadLoot")) return;
                    
                    if (Cache.Instance.CargoHold.IsValid && Cache.Instance.CargoHold.Items.Any())
                    {
                        if (!Cache.Instance.ReadyAmmoHangar("UnloadLoot")) return;

                        if (Cache.Instance.AmmoHangar.IsValid)
                        {
                            
                            //
                            // Add mission item  to the list of things to move
                            //
                            try
                            {
                                commonMissionCompletionItemsToMove = Cache.Instance.CargoHold.Items.Where(i => i.GroupId == (int)Group.Livestock).ToList();
                            }
                            catch (Exception exception)
                            {
                                if (Settings.Instance.DebugUnloadLoot) Logging.Log("Unloadloot", "MoveAmmo: No Mission CompletionItems Found in CargoHold: moving on. [" + exception + "]", Logging.White);
                            }
        
                            if (Settings.Instance.MoveCommonMissionCompletionItemsToItemsHangar)
                            {
                                if (commonMissionCompletionItemsToMove != null)
                                {
                                    if (commonMissionCompletionItemsToMove.Any())
                                    {
                                        if (!Cache.Instance.OpenItemsHangar("UnloadLoot")) return;
                                        Logging.Log("UnloadLoot", "Moving [" + ItemsToMove.Count() + "] Mission Completion items to ItemHangar", Logging.White);
                                        Cache.Instance.ItemHangar.Add(commonMissionCompletionItemsToMove);
                                        AmmoIsBeingMoved = true;
                                        _nextUnloadAction = DateTime.Now.AddSeconds(Cache.Instance.RandomNumber(2, 4));
                                        return;
                                    }
                                }
                            }

                            if (Settings.Instance.MoveCommonMissionCompletionItemsToAmmoHangar)
                            {
                                if (commonMissionCompletionItemsToMove != null)
                                {
                                    if (commonMissionCompletionItemsToMove.Any())
                                    {
                                        if (!Cache.Instance.ReadyAmmoHangar("UnloadLoot")) return;
                                        Logging.Log("UnloadLoot", "Moving [" + commonMissionCompletionItemsToMove.Count() + "] Mission Completion items to AmmoHangar", Logging.White);
                                        Cache.Instance.AmmoHangar.Add(commonMissionCompletionItemsToMove);
                                        _nextUnloadAction = DateTime.Now.AddSeconds(Cache.Instance.RandomNumber(2, 4));
                                        return;
                                    }
                                }
                            }

                            //
                            // Add Ammo to the list of things to move
                            //                        
                            try
                            {
                                ammoToMove = Cache.Instance.CargoHold.Items.Where(i => Settings.Instance.Ammo.All(a => a.TypeId == i.TypeId)).ToList();
                            }
                            catch (Exception exception)
                            {
                                if (Settings.Instance.DebugUnloadLoot) Logging.Log("Unloadloot", "MoveAmmo: No Ammo Found in CargoHold: moving on. [" + exception + "]", Logging.White);
                            }

                            if (ammoToMove != null)
                            {
                                if (ammoToMove.Any())
                                {
                                    if (!Cache.Instance.ReadyAmmoHangar("UnloadLoot")) return;
                                    Logging.Log("UnloadLoot", "Moving [" + ammoToMove.Count() + "] Ammo Stacks to AmmoHangar", Logging.White);
                                    Cache.Instance.AmmoHangar.Add(ammoToMove);
                                    _nextUnloadAction = DateTime.Now.AddSeconds(Cache.Instance.RandomNumber(2, 4));
                                    return;
                                }
                            }

                            //
                            // Add Scripts (by groupID) to the list of things to move
                            //                        

                            try
                            {
                                //
                                // items to move has to be cleared here before assigning but is currently not being cleared here
                                //
                                scriptsToMove = Cache.Instance.CargoHold.Items.Where(i => 
                                    i.GroupId == (int)Group.TrackingScript || 
                                    i.GroupId == (int)Group.WarpDisruptionScript ||
                                    i.GroupId == (int)Group.TrackingDisruptionScript ||
                                    i.GroupId == (int)Group.SensorBoosterScript ||
                                    i.GroupId == (int)Group.SensorDampenerScript ||
                                    i.GroupId == (int)Group.CapacitorGroupCharge).ToList();
                            }
                            catch (Exception exception)
                            {
                                if (Settings.Instance.DebugUnloadLoot) Logging.Log("Unloadloot", "MoveAmmo: No Scripts Found in CargoHold: moving on. [" + exception + "]", Logging.White);
                            }

                            if (scriptsToMove != null)
                            {
                                if (scriptsToMove.Any())
                                {
                                    if (!Cache.Instance.OpenItemsHangar("UnloadLoot")) return;
                                    Logging.Log("UnloadLoot", "Moving [" + scriptsToMove.Count() + "] Scripts to ItemHangar", Logging.White);
                                    Cache.Instance.ItemHangar.Add(scriptsToMove);
                                    _nextUnloadAction = DateTime.Now.AddSeconds(Cache.Instance.RandomNumber(2, 4));
                                    return;
                                }
                            }
                        }
                    }

                    //
                    // Stack AmmoHangar
                    //
                    if (!Cache.Instance.StackAmmoHangar("UnloadLoot.MoveAmmo")) return;

                    break;
            }
        }
    }
}