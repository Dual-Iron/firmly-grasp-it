using BepInEx;
using RWCustom;
using System;
using System.Collections.Generic;
using System.Security.Permissions;
using UnityEngine;
using static AItile;
using static MovementConnection;

#pragma warning disable CS0618 // Do not remove the following line.
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]

namespace FirmGrasp;

[BepInPlugin("com.dual.firmly-grasp-it", "Firmly Grasp It", "1.0.0")]
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
        // This method mimics AImapper.Update at specific tiles

        foreach (var tile in tiles) {
            // Set tiles' accessibility and walkability. Don't need to set fall risk or terrain proximity because adding/removing poles cannot affect that stuff.
            SetAccessibility(room, tile);
        }

        // Have to recalculate entire room's movement connection list, because of stuff like reaching over gaps or falling onto beams that can affect many tiles
        RedoConnections(room);

        // Have to recalculate the entire room's dijkstra unfortunately.
        RedoDijkstra(room);

        // Also start recalculating paths for every critter in the room.
        RedoPathfinders(room);

        Logger.LogDebug($"Pathing updated for {room.abstractRoom.name}");
    }

    private static void SetAccessibility(Room room, IntVector2 pos)
    {
        // Copy from AImapper.FindAccessibilityOfCurrentTile()

        int x = pos.x, y = pos.y;
        Room.Tile tile = room.GetTile(pos);
        AImap map = room.aimap;

        if (tile.Terrain == Room.Tile.TerrainType.Solid) {
            map.map[x, y].acc = Accessibility.Solid;
            map.map[x, y].walkable = false;
            return;
        }

        Accessibility accessibility = Accessibility.Air;
        if (room.GetTile(x, y - 1).Terrain == Room.Tile.TerrainType.Solid || room.GetTile(x, y - 1).Terrain == Room.Tile.TerrainType.Floor
            || (room.GetTile(x - 1, y - 1).Terrain == Room.Tile.TerrainType.Solid && room.GetTile(x - 1, y).Terrain != Room.Tile.TerrainType.Solid)
            || (room.GetTile(x + 1, y - 1).Terrain == Room.Tile.TerrainType.Solid && room.GetTile(x + 1, y).Terrain != Room.Tile.TerrainType.Solid)) {
            accessibility = Accessibility.Floor;
        }
        else if (room.GetTile(x - 1, y).Terrain == Room.Tile.TerrainType.Solid && room.GetTile(x + 1, y).Terrain == Room.Tile.TerrainType.Solid) {
            accessibility = Accessibility.Corridor;
        }
        else if (tile.verticalBeam || tile.horizontalBeam) {
            accessibility = Accessibility.Climb;
        }
        else if (tile.wallbehind || room.GetTile(x - 1, y).Terrain == Room.Tile.TerrainType.Solid || room.GetTile(x + 1, y).Terrain == Room.Tile.TerrainType.Solid
            || (room.GetTile(x - 1, y + 1).Terrain == Room.Tile.TerrainType.Solid && room.GetTile(x, y + 1).Terrain != Room.Tile.TerrainType.Solid)
            || (room.GetTile(x + 1, y + 1).Terrain == Room.Tile.TerrainType.Solid && room.GetTile(x, y + 1).Terrain != Room.Tile.TerrainType.Solid)) {
            accessibility = Accessibility.Wall;
        }
        else if (room.GetTile(x, y + 1).Terrain == Room.Tile.TerrainType.Solid) {
            accessibility = Accessibility.Ceiling;
        }

        map.map[x, y].acc = accessibility;
        map.map[x, y].walkable = accessibility is not Accessibility.Air and not Accessibility.Solid;
    }

    private static void RedoConnections(Room room)
    {
        static bool IsSetByAimapper(MovementType ty)
        {
            // See SetConnections. This includes all movement types that will be recalculated because of a spear's placement.
            return ty switch {
                MovementType.Standard or MovementType.ReachUp or MovementType.DoubleReachUp or MovementType.ReachDown or MovementType.DropToFloor or
                MovementType.DropToClimb or MovementType.DropToWater or MovementType.ReachOverGap or MovementType.LizardTurn or MovementType.Slope or
                MovementType.OpenDiagonal or MovementType.CeilingSlope or MovementType.SemiDiagonalReach => true,
                _ => false,
            };
        }
        static bool IsReset(MovementConnection conn) => IsSetByAimapper(conn.type);

        for (int y = 0; y < room.TileHeight; y++) {
            for (int x = 0; x < room.TileWidth; x++) {
                room.aimap.map[x, y].outgoingPaths.RemoveAll(IsReset);
                room.aimap.map[x, y].incomingPaths.RemoveAll(IsReset);
            }
        }
        for (int y = 0; y < room.TileHeight; y++) {
            for (int x = 0; x < room.TileWidth; x++) {
                SetConnections(room, new(x, y));
            }
        }
    }

    private static void SetConnections(Room room, IntVector2 pos)
    {
        // Copy from AImapper.FindPassagesOfCurrentTile()
        // Not a clue how this works.

        int x = pos.x, y = pos.y;
        AImap map = room.aimap;

        WorldCoordinate WrldCrd(IntVector2 pos) => new(room.abstractRoom.index, pos.x, pos.y, -1);

        bool IsThereAStraightDropBetweenTiles(IntVector2 A, IntVector2 B)
        {
            IntVector2 b = new(0, -1);
            if (map.getAItile(A + b).acc != Accessibility.Solid && map.getAItile(A + b).acc > map.getAItile(A).acc) {
                for (int i = A.y - 1; i > 0; i--) {
                    if (map.getAItile(A.x, i).acc == Accessibility.Floor && (room.GetTile(A.x, i - 1).Terrain == Room.Tile.TerrainType.Solid || room.GetTile(A.x, i - 1).Terrain == Room.Tile.TerrainType.Floor)) {
                        return new IntVector2(A.x, i) == B;
                    }
                    if (map.getAItile(A.x, i).acc == Accessibility.Climb && map.getAItile(A.x, i + 1).acc > map.getAItile(A.x, i).acc && new IntVector2(A.x, i) == B) {
                        return true;
                    }
                }
            }
            return false;
        }

        if (room.GetTile(x, y).Terrain == Room.Tile.TerrainType.Floor || (room.GetTile(x, y).Terrain == Room.Tile.TerrainType.Solid && room.GetTile(x, y + 1).Terrain != Room.Tile.TerrainType.Solid)) {
            int num = y + 1;
            while (num < map.map.GetLength(1) && room.GetTile(x, num).Terrain != Room.Tile.TerrainType.Solid) {
                map.map[x, num].floorAltitude = num - y;
                num++;
            }
        }

        for (int i = 0; i < 4; i++) {
            if (pos.x + Custom.fourDirections[i].x >= 0 && pos.x + Custom.fourDirections[i].x < room.TileWidth && pos.y + Custom.fourDirections[i].y >= 0 && pos.y + Custom.fourDirections[i].y < room.TileHeight) {
                map.map[x, y].outgoingPaths.Add(new(MovementType.Standard, WrldCrd(pos), WrldCrd(pos + Custom.fourDirections[i]), 1));
            }
            if (room.GetTile(pos + Custom.eightDirections[i]).Terrain == Room.Tile.TerrainType.Solid && room.GetTile(pos - Custom.eightDirections[i]).Terrain == Room.Tile.TerrainType.Solid) {
                map.map[x, y].narrowSpace = true;
            }
        }

        IntVector2 up = new(0, 1);

        if (map.getAItile(pos + up).acc != Accessibility.Solid && map.getAItile(pos + up * 2).walkable && map.getAItile(pos + up).acc > map.getAItile(pos).acc && map.getAItile(pos + up * 2).acc < map.getAItile(pos + up).acc) {
            map.map[x, y].outgoingPaths.Add(new(MovementType.ReachUp, WrldCrd(pos), WrldCrd(pos + up * 2), 2));
        }
        if (map.getAItile(pos + up * 3).walkable && map.getAItile(pos + up).acc != Accessibility.Solid && map.getAItile(pos + up * 2).acc != Accessibility.Solid && map.getAItile(pos + up).acc > map.getAItile(pos).acc && map.getAItile(pos + up).acc > map.getAItile(pos + up * 3).acc && map.getAItile(pos + up * 2).acc > map.getAItile(pos).acc && map.getAItile(pos + up * 2).acc > map.getAItile(pos + up * 3).acc) {
            map.map[x, y].outgoingPaths.Add(new(MovementType.DoubleReachUp, WrldCrd(pos), WrldCrd(pos + up * 3), 3));
        }
        if (map.getAItile(pos - up).acc != Accessibility.Solid && map.getAItile(pos - up * 2).walkable && map.getAItile(pos - up).acc > map.getAItile(pos).acc && map.getAItile(pos - up * 2).acc < map.getAItile(pos + up).acc) {
            map.map[x, y].outgoingPaths.Add(new(MovementType.ReachDown, WrldCrd(pos), WrldCrd(pos - up * 2), 2));
        }

        IntVector2 down = new(0, -1);

        if (map.getAItile(pos + down).acc != Accessibility.Solid && map.getAItile(pos + down).acc > map.getAItile(pos).acc) {
            int num2 = 0;
            List<int> list = new();
            for (int j = pos.y - 1; j > 0; j--) {
                if (map.getAItile(pos.x, j).acc == Accessibility.Floor && (room.GetTile(pos.x, j - 1).Terrain == Room.Tile.TerrainType.Solid || room.GetTile(pos.x, j - 1).Terrain == Room.Tile.TerrainType.Floor)) {
                    map.map[x, y].outgoingPaths.Add(new(MovementType.DropToFloor, WrldCrd(pos), WrldCrd(new IntVector2(pos.x, j)), pos.y - j));
                    list.Add(j);
                    list.Add(j - 1);
                    list.Add(j + 1);
                    break;
                }
                if (map.getAItile(pos.x, j).acc == Accessibility.Climb && map.getAItile(pos.x, j + 1).acc > map.getAItile(pos.x, j).acc) {
                    map.map[x, y].outgoingPaths.Add(new(MovementType.DropToClimb, WrldCrd(pos), WrldCrd(new IntVector2(pos.x, j)), pos.y - j));
                    list.Add(j);
                }
                if (room.GetTile(pos.x, j).WaterSurface) {
                    map.map[x, y].outgoingPaths.Add(new(MovementType.DropToWater, WrldCrd(pos), WrldCrd(new IntVector2(pos.x, j)), pos.y - j));
                    list.Add(j);
                }
                num2++;
            }
            if (num2 >= 4) {
                for (int k = 1; k >= -1; k -= 2) {
                    int num3 = pos.y - 4;
                    while (num3 > 0 && map.getAItile(pos.x + k, num3).acc != Accessibility.Solid && map.getAItile(pos.x, num3).acc != Accessibility.Solid) {
                        if (map.getAItile(pos.x + k, num3).acc == Accessibility.Floor && map.getAItile(pos.x + k, num3 + 1).acc != Accessibility.Solid && map.getAItile(pos.x + k, num3 + 2).acc != Accessibility.Solid && (room.GetTile(pos.x + k, num3 - 1).Terrain == Room.Tile.TerrainType.Solid || room.GetTile(pos.x + k, num3 - 1).Terrain == Room.Tile.TerrainType.Floor) && !IsThereAStraightDropBetweenTiles(new IntVector2(pos.x - 1, pos.y), new IntVector2(pos.x + k, num3)) && !IsThereAStraightDropBetweenTiles(new IntVector2(pos.x + 1, pos.y), new IntVector2(pos.x + k, num3))) {
                            if (!list.Contains(num3)) {
                                map.map[x, y].outgoingPaths.Add(new(MovementType.DropToFloor, WrldCrd(pos), WrldCrd(new IntVector2(pos.x + k, num3)), pos.y - num3));
                                break;
                            }
                            break;
                        }
                        else {
                            if (map.getAItile(pos.x + k, num3).acc == Accessibility.Climb && map.getAItile(pos.x + k, num3 + 1).acc != Accessibility.Solid && map.getAItile(pos.x + k, num3 + 2).acc != Accessibility.Solid && map.getAItile(pos.x + k, num3 + 1).acc > map.getAItile(pos.x + k, num3).acc && !IsThereAStraightDropBetweenTiles(new IntVector2(pos.x - 1, pos.y), new IntVector2(pos.x + k, num3)) && !IsThereAStraightDropBetweenTiles(new IntVector2(pos.x + 1, pos.y), new IntVector2(pos.x + k, num3)) && !list.Contains(num3)) {
                                map.map[x, y].outgoingPaths.Add(new(MovementType.DropToClimb, WrldCrd(pos), WrldCrd(new IntVector2(pos.x + k, num3)), pos.y - num3));
                            }
                            num3--;
                        }
                    }
                }
            }
        }
        for (int l = 0; l < 2; l++) {
            IntVector2 h = new(l == 0 ? -1 : 1, 0);

            if (map.getAItile(pos + h).acc != Accessibility.Solid && map.getAItile(pos + h * 2).walkable && map.getAItile(pos + h).acc > map.getAItile(pos).acc && map.getAItile(pos + h * 2).acc < map.getAItile(pos + h).acc) {
                map.map[x, y].outgoingPaths.Add(new(MovementType.ReachOverGap, WrldCrd(pos), WrldCrd(pos + h * 2), 2));
            }
            if (map.getAItile(pos).acc == Accessibility.Floor && map.getAItile(pos + h).acc == Accessibility.Floor && map.getAItile(pos + new IntVector2(0, 1)).acc != Accessibility.Solid && map.getAItile(pos + h + new IntVector2(0, 1)).acc != Accessibility.Solid) {
                map.map[x, y].outgoingPaths.Add(new(MovementType.LizardTurn, WrldCrd(pos), WrldCrd(pos + h), 1));
            }
            if (map.getAItile(pos).acc == Accessibility.Floor && room.GetTile(pos + h).Terrain == Room.Tile.TerrainType.Slope && map.getAItile(pos + new IntVector2(h.x, 1)).acc == Accessibility.Floor) {
                map.map[x, y].outgoingPaths.Add(new(MovementType.Slope, WrldCrd(pos), WrldCrd(pos + new IntVector2(h.x, 1)), 1));
                map.map[x + h.x, y + 1].outgoingPaths.Add(new(MovementType.Slope, WrldCrd(pos + new IntVector2(h.x, 1)), WrldCrd(pos), 1));
            }
            else if (map.getAItile(pos + new IntVector2(h.x, 0)).acc != Accessibility.Solid && map.getAItile(pos + new IntVector2(0, 1)).acc != Accessibility.Solid) {
                int num4 = Math.Max((int)map.getAItile(pos).acc, (int)map.getAItile(pos + new IntVector2(h.x, 1)).acc);
                if (map.getAItile(pos + new IntVector2(h.x, 0)).acc > (Accessibility)num4 && map.getAItile(pos + new IntVector2(0, 1)).acc > (Accessibility)num4) {
                    map.map[x, y].outgoingPaths.Add(new(MovementType.OpenDiagonal, WrldCrd(pos), WrldCrd(pos + new IntVector2(h.x, 1)), 1));
                    map.map[x + h.x, y + 1].outgoingPaths.Add(new(MovementType.OpenDiagonal, WrldCrd(pos + new IntVector2(h.x, 1)), WrldCrd(pos), 1));
                }
            }
            if ((map.getAItile(pos).acc == Accessibility.Ceiling || map.getAItile(pos).acc == Accessibility.Wall) && room.GetTile(pos + h).Terrain == Room.Tile.TerrainType.Slope && (map.getAItile(pos + new IntVector2(h.x, -1)).acc == Accessibility.Ceiling || map.getAItile(pos + new IntVector2(h.x, -1)).acc == Accessibility.Wall)) {
                map.map[x, y].outgoingPaths.Add(new(MovementType.CeilingSlope, WrldCrd(pos), WrldCrd(pos + new IntVector2(h.x, -1)), 1));
                map.map[x + h.x, y - 1].outgoingPaths.Add(new(MovementType.CeilingSlope, WrldCrd(pos + new IntVector2(h.x, -1)), WrldCrd(pos), 1));
                map.map[x, y].outgoingPaths.Add(map.map[x + h.x, y - 1].outgoingPaths[map.map[x + h.x, y - 1].outgoingPaths.Count - 1]);
            }
            if (map.getAItile(pos + new IntVector2(h.x, -2)).walkable && map.getAItile(pos + new IntVector2(0, -1)).acc != Accessibility.Solid && map.getAItile(pos + new IntVector2(h.x, -1)).acc != Accessibility.Solid && (map.getAItile(pos + new IntVector2(h.x, 0)).acc != Accessibility.Solid || map.getAItile(pos + new IntVector2(0, -2)).acc != Accessibility.Solid) && map.getAItile(pos + new IntVector2(h.x, 0)).acc > map.getAItile(pos).acc && map.getAItile(pos + new IntVector2(h.x, 0)).acc > map.getAItile(pos + new IntVector2(h.x, -2)).acc && map.getAItile(pos + new IntVector2(0, -2)).acc > map.getAItile(pos).acc && map.getAItile(pos + new IntVector2(0, -2)).acc > map.getAItile(pos + new IntVector2(h.x, -2)).acc && map.getAItile(pos + new IntVector2(0, -1)).acc > map.getAItile(pos).acc && map.getAItile(pos + new IntVector2(0, -1)).acc > map.getAItile(pos + new IntVector2(h.x, -2)).acc && map.getAItile(pos + new IntVector2(h.x, -1)).acc > map.getAItile(pos).acc && map.getAItile(pos + new IntVector2(h.x, -1)).acc > map.getAItile(pos + new IntVector2(h.x, -2)).acc) {
                map.map[x, y].outgoingPaths.Add(new(MovementType.SemiDiagonalReach, WrldCrd(pos), WrldCrd(pos + new IntVector2(h.x, -2)), 3));
                map.map[x + h.x, y - 2].outgoingPaths.Add(new(MovementType.SemiDiagonalReach, WrldCrd(pos + new IntVector2(h.x, -2)), WrldCrd(pos), 3));
                map.map[x, y].outgoingPaths.Add(map.map[x + h.x, y - 2].outgoingPaths[map.map[x + h.x, y - 2].outgoingPaths.Count - 1]);
            }
            if (map.getAItile(pos + new IntVector2(-2, h.x)).walkable && map.getAItile(pos + new IntVector2(-1, 0)).acc != Accessibility.Solid && map.getAItile(pos + new IntVector2(-1, h.x)).acc != Accessibility.Solid && (map.getAItile(pos + new IntVector2(0, h.x)).acc != Accessibility.Solid || map.getAItile(pos + new IntVector2(-2, 0)).acc != Accessibility.Solid) && map.getAItile(pos + new IntVector2(0, h.x)).acc > map.getAItile(pos).acc && map.getAItile(pos + new IntVector2(0, h.x)).acc > map.getAItile(pos + new IntVector2(-2, h.x)).acc && map.getAItile(pos + new IntVector2(-2, 0)).acc > map.getAItile(pos).acc && map.getAItile(pos + new IntVector2(-2, 0)).acc > map.getAItile(pos + new IntVector2(-2, h.x)).acc && map.getAItile(pos + new IntVector2(-1, 0)).acc > map.getAItile(pos).acc && map.getAItile(pos + new IntVector2(-1, 0)).acc > map.getAItile(pos + new IntVector2(-2, h.x)).acc && map.getAItile(pos + new IntVector2(-1, h.x)).acc > map.getAItile(pos).acc && map.getAItile(pos + new IntVector2(-1, h.x)).acc > map.getAItile(pos + new IntVector2(-2, h.x)).acc) {
                map.map[x, y].outgoingPaths.Add(new(MovementType.SemiDiagonalReach, WrldCrd(pos), WrldCrd(pos + new IntVector2(-2, h.x)), 3));
                map.map[x - 2, y + h.x].outgoingPaths.Add(new(MovementType.SemiDiagonalReach, WrldCrd(pos + new IntVector2(-2, h.x)), WrldCrd(pos), 3));
                map.map[x, y].outgoingPaths.Add(map.map[x - 2, y + h.x].outgoingPaths[map.map[x - 2, y + h.x].outgoingPaths.Count - 1]);
            }
        }
        foreach (var movementConnection in map.map[x, y].outgoingPaths) {
            map.map[movementConnection.destinationCoord.x, movementConnection.destinationCoord.y].incomingPaths.Add(movementConnection);
        }
        return;
    }

    private static void RedoDijkstra(Room room)
    {
        AIdataPreprocessor processor = new(room.aimap, falseBake: true);

        while (!processor.done) {
            processor.Update();
        }
    }

    private static void RedoPathfinders(Room room)
    {
        foreach (var crit in room.abstractRoom.creatures) {
            var pf = crit.abstractAI?.RealAI?.pathFinder;
            if (pf == null) {
                continue;
            }

            pf.reAssignDestinationOnceAccessibilityMappingIsDone = true;
            pf.InitiAccessibilityMapping(crit.pos, pf.accessibilityMapper?.alreadyCheckedTiles);
        }
    }
}
