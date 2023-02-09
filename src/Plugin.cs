using BepInEx;
using RWCustom;
using System;
using System.Linq;
using System.Security.Permissions;
using UnityEngine;

#pragma warning disable CS0618 // Do not remove the following line.
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]

namespace FirmGrasp;

[BepInPlugin("com.dual.firmly-grasp-it", "Firmly Grasp It", "1.0.1")]
sealed class Plugin : BaseUnityPlugin
{
    private static IntVector2[] GetStuckTiles(Spear self)
    {
        if (self.abstractSpear.stuckInWallCycles != 0 && self.stuckInWall is Vector2 posExact) {
            IntVector2 pos = self.room.GetTilePosition(posExact);

            // Cycles > 0 indicates horizontal
            if (self.abstractSpear.stuckInWallCycles > 0) {
                return new IntVector2[] { pos + new IntVector2(-1, 0), pos, pos + new IntVector2(1, 0) };
            }
            return new IntVector2[] { pos + new IntVector2(0, -1), pos, pos + new IntVector2(0, 1) };
        }
        return Array.Empty<IntVector2>(); ;
    }

    public void OnEnable()
    {
        On.Spear.Update += Spear_Update;
        On.Spear.resetHorizontalBeamState += Spear_resetHorizontalBeamState;
    }

    private void Spear_Update(On.Spear.orig_Update orig, Spear self, bool eu)
    {
        bool addPoles = self.addPoles;

        orig(self, eu);

        if (addPoles && !self.addPoles) {
            // Poles were added!

            UpdateMap(self.room, GetStuckTiles(self));
        }
    }

    private void Spear_resetHorizontalBeamState(On.Spear.orig_resetHorizontalBeamState orig, Spear self)
    {
        IntVector2[] tiles = GetStuckTiles(self);

        orig(self);

        UpdateMap(self.room, tiles);
    }

    private void UpdateMap(Room room, IntVector2[] tiles)
    {
        room.AddObject(new PathRefresher(Logger, tiles) {
            WaitFor = room.updateList.OfType<PathRefresher>().LastOrDefault(),
        });
    }
}
