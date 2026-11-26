import type { Clues } from "./nonogram-logic";

export interface InkPlate extends Clues {
  id: string;
  title: string;
  caption: string;
  pigment: string;
}

export const inkPlates: readonly InkPlate[] = [
  {
    id: "plate-01", title: "Temple Bell", caption: "One bronze note at closing time.", pigment: "#854532",
    rows: [[1], [3], [3], [5], [1]],
    columns: [[1], [3], [5], [3], [1]],
  },
  {
    id: "plate-02", title: "Paper Sail", caption: "A folded boat takes the evening wind.", pigment: "#395864",
    rows: [[1], [2], [3], [1], [5]],
    columns: [[1], [1], [5], [2, 1], [1, 1]],
  },
  {
    id: "plate-03", title: "Brass Key", caption: "The copy that opens the print room.", pigment: "#825727",
    rows: [[3], [1, 1], [3], [1], [3]],
    columns: [[3], [1, 1], [5], [1], [1]],
  },
  {
    id: "plate-04", title: "Ember Flame", caption: "A last curl of heat above the coals.", pigment: "#984439",
    rows: [[1], [2], [3], [5], [3]],
    columns: [[1], [4], [5], [3], [1]],
  },
  {
    id: "plate-05", title: "Plum Teapot", caption: "A chipped pot still pours a fine cup.", pigment: "#73506b",
    rows: [[2], [4], [2], [7], [5, 1], [5], [5], [3]],
    columns: [[1], [2], [1, 4], [8], [8], [1, 5], [1, 2], [1]],
  },
  {
    id: "plate-06", title: "Reed Kite", caption: "A paper diamond tugs on its tail.", pigment: "#405a55",
    rows: [[2], [4], [6], [4], [2], [1], [2], [2]],
    columns: [[], [1], [3, 1], [5, 2], [7], [3], [1], []],
  },
  {
    id: "plate-07", title: "Night Moth", caption: "Brief wings at the edge of lamplight.", pigment: "#554b70",
    rows: [[2, 1], [2, 2], [3, 3], [8], [6], [4], [1, 2, 1], [2]],
    columns: [[4], [5, 1], [4], [5], [5], [4], [4, 1], [4]],
  },
  {
    id: "plate-08", title: "Stone Bridge", caption: "A single arch reflected in the canal.", pigment: "#586448",
    rows: [[], [4], [6], [3, 3], [2, 2], [2, 2], [3, 3], [8]],
    columns: [[5], [6], [3, 2], [2, 1], [2, 1], [3, 2], [6], [5]],
  },
  {
    id: "plate-09", title: "Autumn Leaf", caption: "Pressed flat between two sheets of proof paper.", pigment: "#8e462c",
    rows: [[1], [3, 1], [2, 2], [1, 6], [8], [7], [5], [3], [1], [2]],
    columns: [[], [2], [2], [6], [8], [1, 6], [5, 1], [5, 1], [5], []],
  },
  {
    id: "plate-10", title: "Reedbed Heron", caption: "Still as the reeds until the water moves.", pigment: "#35566a",
    rows: [[3], [2, 1], [2], [2], [5], [7], [8], [6], [1, 1], [1, 1]],
    columns: [[1], [3], [3], [6], [4], [6], [8], [7], [1], [1]],
  },
  {
    id: "plate-11", title: "Canal Carp", caption: "A dark fin cuts through the mirror of the water.", pigment: "#9a463a",
    rows: [[4], [6, 1], [9], [3, 6], [9], [9], [9], [6, 1], [4], [1]],
    columns: [[3], [5], [7], [3, 5], [10], [9], [9], [7], [5], [3, 2]],
  },
  {
    id: "plate-12", title: "Mountain Dawn", caption: "The ridge comes into view before the road.", pigment: "#52614a",
    rows: [[2], [4], [2], [1], [3, 1], [5, 3], [10], [5, 4], [4, 3], [10]],
    columns: [[5], [6], [7], [6], [3, 1], [1, 1], [1, 3, 1], [3, 6], [3, 5], [1, 4]],
  },
];
