using System;
using System.Collections.Generic;
using BepInEx.Logging;
using EFT;
using EFT.InventoryLogic;

namespace ImmersiveVoice
{
    // Traverse only this player's carried equipment, including nested known
    // containers. Unknown/unsearched contents cannot prove that supplies are absent.
    internal sealed class MedicalSupplyProbe
    {
        private const int ItemLimit = 512;
        private readonly Player _player;
        private readonly ManualLogSource _log;
        private readonly Stack<Item> _pending = new Stack<Item>();
        private bool _faulted;
        public MedicalSupplyProbe(Player player, ManualLogSource log) { _player = player; _log = log; }

        public MedicalSupplies Read()
        {
            if (_faulted || _player == null) return MedicalSupplies.Unknown;
            try
            {
                var inventory = _player.InventoryController?.Inventory;
                if (inventory?.Equipment == null || _player.HealthController == null) return MedicalSupplies.Unknown;
                _pending.Push(inventory.Equipment);
                bool uncertain = false;
                int visited = 0;
                while (_pending.Count > 0)
                {
                    if (++visited > ItemLimit) return MedicalSupplies.Unknown;
                    Item item = _pending.Pop();
                    if (item == null || item is UnknownItem) { uncertain = true; continue; }
                    if (item is SearchableItem searchable && !_player.InventoryController.SearchController.IsSearched(searchable))
                    { uncertain = true; continue; }
                    var resource = item.GetItemComponent<MedKitComponent>();
                    if (item is Meds || resource != null
                        || (!(item is FoodDrink) && item.GetItemComponent<HealthEffectsComponent>() != null))
                    {
                        MedicalSupplies state = MedicalRequestPolicy.Medicine(resource != null, resource?.HpResource ?? 0f,
                            resource != null && _player.HealthController.CanApplyItem(item, EBodyPart.Common));
                        if (state == MedicalSupplies.Available) return state;
                        uncertain |= state == MedicalSupplies.Unknown;
                    }
                    if (!(item is ContainerCollection collection)) continue;
                    foreach (var container in collection.Containers)
                        foreach (var child in container.Items)
                        {
                            if (visited + _pending.Count >= ItemLimit) return MedicalSupplies.Unknown;
                            _pending.Push(child);
                        }
                }
                return uncertain ? MedicalSupplies.Unknown : MedicalSupplies.Missing;
            }
            catch (Exception ex)
            {
                _faulted = true;
                _log.LogWarning("Medical supply checks disabled for this raid; other voice features continue: " + ex.Message);
                return MedicalSupplies.Unknown;
            }
            finally { _pending.Clear(); }
        }
    }
}
