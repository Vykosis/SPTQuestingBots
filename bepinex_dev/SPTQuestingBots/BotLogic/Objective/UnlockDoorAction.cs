﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EFT.Interactive;
using EFT;
using SPTQuestingBots.Controllers;
using UnityEngine.AI;
using UnityEngine;
using System.Reflection;
using Comfort.Common;
using EFT.InventoryLogic;
using Aki.Reflection.Utils;
using HarmonyLib;
using System.Collections;
using System.Threading;

namespace SPTQuestingBots.BotLogic.Objective
{
    public class UnlockDoorAction : BehaviorExtensions.GoToPositionAbstractAction
    {
        private Door door = null;
        private Vector3? interactionPosition = null;
        private IResult keyGenerationResult = null;
        private KeyComponent keyComponent = null;
        private DependencyGraph<IEasyBundle>.GClass3114 bundleLoader = null;

        public UnlockDoorAction(BotOwner _BotOwner) : base(_BotOwner, 100)
        {
            
        }

        public override void Start()
        {
            base.Start();

            BotOwner.PatrollingData.Pause();

            door = ObjectiveManager.GetCurrentQuestInteractiveObject() as Door;
            if (door == null)
            {
                LoggingController.LogError("Cannot unlock a null door");

                ObjectiveManager.FailObjective();

                return;
            }

            interactionPosition = getInteractionPosition(door);
            if (interactionPosition == null)
            {
                LoggingController.LogError("Cannot find the appropriate interaction position for door " + door.Id);

                ObjectiveManager.FailObjective();

                return;
            }

            if (!tryTransferKeyToBot())
            {
                ObjectiveManager.FailObjective();

                return;
            }
        }

        public override void Stop()
        {
            base.Stop();

            BotOwner.PatrollingData.Unpause();
        }

        public override void Update()
        {
            UpdateBotMovement(CanSprint);
            UpdateBotSteering();

            // Don't allow expensive parts of this behavior to run too often
            if (!canUpdate())
            {
                return;
            }

            ObjectiveManager.StartJobAssigment();

            if (door.DoorState != EDoorState.Locked)
            {
                LoggingController.LogWarning("Door " + ObjectiveManager.GetCurrentQuestInteractiveObject().Id + " is already unlocked");

                ObjectiveManager.DoorIsUnlocked();

                return;
            }

            if (keyGenerationResult?.Failed == true)
            {
                ObjectiveManager.FailObjective();

                return;
            }

            if (checkIfBotIsStuck())
            {
                LoggingController.LogWarning(BotOwner.GetText() + " got stuck while trying to unlock door " + ObjectiveManager.GetCurrentQuestInteractiveObject().Id + ". Giving up.");

                if (ObjectiveManager.TryChangeObjective())
                {
                    restartStuckTimer();
                }

                return;
            }

            if (!ObjectiveManager.MustUnlockDoor)
            {
                return;
            }

            // TO DO: Can this distance be reduced?
            float distanceToTargetPosition = Vector3.Distance(BotOwner.Position, interactionPosition.Value);
            if (distanceToTargetPosition >= 0.5f)
            {
                NavMeshPathStatus? pathStatus = RecalculatePath(interactionPosition.Value);

                if (!pathStatus.HasValue || (pathStatus.Value == NavMeshPathStatus.PathInvalid))
                {
                    LoggingController.LogWarning(BotOwner.GetText() + " cannot find a complete path to door " + ObjectiveManager.GetCurrentQuestInteractiveObject().Id);

                    ObjectiveManager.FailObjective();

                    if (ConfigController.Config.Debug.ShowFailedPaths)
                    {
                        drawBotPath(Color.yellow);
                    }
                }

                return;
            }

            if (keyGenerationResult?.Succeed != true)
            {
                return;
            }

            if (keyComponent == null)
            {
                keyComponent = getKeyComponent();
            }

            if (bundleLoader == null)
            {
                LoggingController.LogInfo("Loading bundle for " + keyComponent.Item.LocalizedName() + "...");
                loadBundle(keyComponent.Item);

                return;
            }

            if (!bundleLoader.Finished)
            {
                LoggingController.LogInfo("Waiting for bundle to load...");

                return;
            }

            unlockDoor(door, keyComponent, EInteractionType.Unlock);
            ObjectiveManager.DoorIsUnlocked();
            LoggingController.LogInfo("Bot " + BotOwner.GetText() + " unlocked door " + door.Id);
        }

        private Vector3? getInteractionPosition(Door door)
        {
            float searchDistance = 0.75f;
            Vector3[] possibleInteractionPositions = new Vector3[4]
            {
                door.transform.position + new Vector3(searchDistance, 0, 0),
                door.transform.position - new Vector3(searchDistance, 0, 0),
                door.transform.position + new Vector3(0, 0, searchDistance),
                door.transform.position - new Vector3(0, 0, searchDistance)
            };

            foreach (Vector3 possibleInteractionPosition in possibleInteractionPositions)
            {
                Vector3? navMeshPosition = LocationController.FindNearestNavMeshPosition(possibleInteractionPosition, ConfigController.Config.Questing.QuestGeneration.NavMeshSearchDistanceSpawn);
                if (!navMeshPosition.HasValue)
                {
                    continue;
                }

                LoggingController.LogInfo(BotOwner.GetText() + " is checking the accessibility of position " + navMeshPosition.Value.ToString() + " for door " + door.Id + "...");

                NavMeshPath path = new NavMeshPath();
                NavMesh.CalculatePath(BotOwner.Position, navMeshPosition.Value, NavMesh.AllAreas, path);

                if (path.status == NavMeshPathStatus.PathComplete)
                {
                    return navMeshPosition;
                }
            }

            return null;
        }

        private bool tryTransferKeyToBot()
        {
            try
            {
                Type playerType = typeof(Player);

                FieldInfo inventoryControllerField = playerType.GetField("_inventoryController", BindingFlags.NonPublic | BindingFlags.Instance);
                InventoryControllerClass botInventoryController = (InventoryControllerClass)inventoryControllerField.GetValue(BotOwner.GetPlayer);

                // Not sure if this should use false
                MongoID IDGenerator = new MongoID(false);

                Item keyItem = Singleton<ItemFactory>.Instance.CreateItem(IDGenerator, door.KeyId, null);
                if (keyItem == null)
                {
                    LoggingController.LogError("Cannot create key for door " + door.Id + " for " + BotOwner.GetText());
                    return false;
                }

                ItemContainerClass secureContainer = botInventoryController.Inventory.Equipment.GetSlot(EquipmentSlot.SecuredContainer).ContainedItem as ItemContainerClass;

                ItemAddress locationForItem = null;
                foreach (GClass2318 secureContainerGrid in (secureContainer?.Grids ?? (new GClass2318[0])))
                {
                    LocationInGrid locationInGrid = secureContainerGrid.FindFreeSpace(keyItem);
                    if (locationInGrid == null)
                    {
                        continue;
                    }

                    locationForItem = new GClass2580(secureContainerGrid, locationInGrid);
                }

                if (locationForItem == null)
                {
                    LoggingController.LogWarning("Cannot find secure-container location to put key " + keyItem.LocalizedName() + " for " + BotOwner.GetText());

                    locationForItem = botInventoryController.FindGridToPickUp(keyItem, botInventoryController);
                }
                if (locationForItem == null)
                {
                    LoggingController.LogError("Cannot find any location to put key " + keyItem.LocalizedName() + " for " + BotOwner.GetText());
                    return false;
                }

                //GStruct375<GClass2597> moveResult = GClass2585.Move(keyItem, locationForItem, botInventoryController, true);
                GStruct375<GClass2593> moveResult = GClass2585.Add(keyItem, locationForItem, botInventoryController, true);
                if (!moveResult.Succeeded)
                {
                    LoggingController.LogError("Cannot move key " + keyItem.LocalizedName() + " to inventory of " + BotOwner.GetText());
                    return false;
                }

                Callback callback = new Callback(transferredKeyCallback);
                botInventoryController.TryRunNetworkTransaction(moveResult, callback);

                return true;
            }
            catch (Exception e)
            {
                LoggingController.LogError(e.Message);
                LoggingController.LogError(e.StackTrace);

                ObjectiveManager.TryChangeObjective();

                throw;
            }
        }

        private void transferredKeyCallback(IResult result)
        {
            keyGenerationResult = result;

            if (result.Succeed)
            {
                LoggingController.LogInfo("Moved key to inventory of " + BotOwner.GetText());
            }

            if (result.Failed)
            {
                LoggingController.LogError("Could not move key to inventory of " + BotOwner.GetText());
            }
        }

        private void loadBundle(Item item)
        {
            if (item == null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            if (item.Prefab.path.Length == 0)
            {
                throw new InvalidOperationException("The prefab path for " + item.LocalizedName() + " is empty");
            }

            bundleLoader = Singleton<IEasyAssets>.Instance.Retain(new string[] { item.Prefab.path }, null, default(CancellationToken));
        }

        private KeyComponent getKeyComponent()
        {
            Type playerType = typeof(Player);

            FieldInfo inventoryControllerField = playerType.GetField("_inventoryController", BindingFlags.NonPublic | BindingFlags.Instance);
            InventoryControllerClass botInventoryController = (InventoryControllerClass)inventoryControllerField.GetValue(BotOwner.GetPlayer);

            IEnumerable<KeyComponent> matchingKeys = botInventoryController.Inventory.Equipment
                .GetItemComponentsInChildren<KeyComponent>(false);

            if (!matchingKeys.Any())
            {
                throw new InvalidOperationException(BotOwner.GetText() + " does not have a key for door " + door.Id);
            }

            return matchingKeys.First();
        }

        private void unlockDoor(Door door, KeyComponent key, EInteractionType interactionType)
        {
            try
            {
                if (door == null)
                {
                    throw new ArgumentNullException(nameof(door));
                }

                if (key == null)
                {
                    throw new ArgumentNullException(nameof(key));
                }

                //BotOwner.DoorOpener.Interact(door, interactionType);

                Type doorOpenerType = typeof(BotDoorOpener);

                PropertyInfo interactingProperty = doorOpenerType.GetProperty("Interacting", BindingFlags.Public | BindingFlags.Instance);
                interactingProperty.SetValue(BotOwner.DoorOpener, true);

                float _traversingEnd = Time.time + BotOwner.Settings.FileSettings.Move.WAIT_DOOR_OPEN_SEC;

                FieldInfo traversingEndField = doorOpenerType.GetField("_traversingEnd", BindingFlags.NonPublic | BindingFlags.Instance);
                traversingEndField.SetValue(BotOwner.DoorOpener, _traversingEnd);

                GClass2761 unlockDoorInteractionResult = new GClass2761(key, null, true);
                if (unlockDoorInteractionResult == null)
                {
                    throw new InvalidOperationException(BotOwner.GetText() + " cannot use key " + key.Item.LocalizedName() + " to unlock door " + door.Id);
                }

                LoggingController.LogInfo(BotOwner.GetText() + " is unlocking door " + door.Id + "...");
                BotOwner.GetPlayer.CurrentManagedState.StartDoorInteraction(door, unlockDoorInteractionResult, null);
            }
            catch (Exception e)
            {
                LoggingController.LogError(e.Message);
                LoggingController.LogError(e.StackTrace);

                ObjectiveManager.TryChangeObjective();

                throw;
            }
        }
    }
}