# Koi-Koi

Koi-Koi is a Japanese matching game played with a 48-card Hanafuda deck.
At Jade Court, play six rounds against one bot or one invited guest. Both players
start at zero and the higher total wins; equal totals tie. The points stay inside
the match.

## Plants and cards

Every month has exactly four cards. Match the **month**, regardless of card kind.
Brights, Animals, Ribbons, and Plains describe what a card can contribute to
captured combinations, not what it can match. “Animal” also includes the bridge
and the cup; Willow's Lightning is a plain card, not a wild card.

| Month | Plant | IDs | Motifs in ID order |
| --- | --- | --- | --- |
| 1 | Pine | 0–3 | Crane, red poetry ribbon, two plains |
| 2 | Plum | 4–7 | Bush warbler, red poetry ribbon, two plains |
| 3 | Cherry | 8–11 | Curtain, red poetry ribbon, two plains |
| 4 | Wisteria | 12–15 | Cuckoo, red ribbon, two plains |
| 5 | Iris | 16–19 | Eight-plank bridge, red ribbon, two plains |
| 6 | Peony | 20–23 | Butterfly, blue ribbon, two plains |
| 7 | Bush Clover | 24–27 | Boar, red ribbon, two plains |
| 8 | Pampas | 28–31 | Moon, geese, two plains |
| 9 | Chrysanthemum | 32–35 | Sake cup, blue ribbon, two plains |
| 10 | Maple | 36–39 | Deer, blue ribbon, two plains |
| 11 | Willow | 40–43 | Rainman, swallow, red ribbon, lightning |
| 12 | Paulownia | 44–47 | Phoenix, three plains |

## Deal and turn

The first dealer is chosen uniformly. Each deal gives each player eight private
cards, puts eight cards face up in the field, and leaves 24 in the face-down stock.
If a field contains all four cards of a month, shuffle and deal again. The engine
tries at most 32 shuffles for each round; if none is usable, the match does not start.
There is no instant six-point award for four cards of a month or four pairs in an
opening hand: play those hands normally.

The dealer goes first. Play any one of your cards and match it to the field. The
top stock card then flips and matches against the **updated** field. The same rules
apply to each card:

| Same-month cards on the field | Outcome |
| --- | --- |
| None | Add the card to the field. |
| One | Capture both cards. |
| Two | Choose one to capture with the played or drawn card; leave the other. |
| Three | Capture all four cards of that month. |

Only after both parts of the turn are resolved does the game check for a scoring
decision. A player who has not called Koi-Koi qualifies with any nonzero yaku
subtotal. A player who has called qualifies only by **increasing** the subtotal
they had when they called; more captures with the same subtotal are not enough.
If neither condition applies, play passes to the other seat.

- **Bank points (Shōbu):** End the round and add the award to your match total.
- **Koi-Koi:** Give up the chance to bank now and pass the turn. Jade Court allows
  one call per round in total, and only when the caller has another card in hand.
  The next player to qualify can only bank. The caller might bank an increased
  award, lose to the other player, or finish with nothing on a draw.

On a player's last personal turn, a qualifying subtotal can be banked but cannot
be risked again, even if the other player has one turn left. If the last turn
qualifies, it still opens a bank decision. If both hands run out with no eligible
increase, the round is a draw, even if the caller still shows unbanked yaku.
The remaining eight stock cards then go unused. A winner deals the next round;
on a draw the deal passes to the other seat.

## Yaku

Only the highest Bright combination counts. The named Animal and Ribbon
combinations replace their respective count awards. Other entries add together.

| Combination | Captured cards | Points |
| --- | --- | --- |
| Five Brights | Crane, Curtain, Moon, Rainman, Phoenix | 10 |
| Four Brights | Four Brights without Rainman | 8 |
| Rainy Four Brights | Four Brights including Rainman | 7 |
| Three Brights | Three Brights without Rainman | 5 |
| Boar, Deer, Butterfly | Those three Animals | 5, plus 1 per additional Animal |
| Animals | At least five Animals; no Boar–Deer–Butterfly | 1 at five, plus 1 per additional Animal |
| Red and Blue Ribbons | All six named ribbons below | 10, plus 1 per ribbon beyond those six |
| Red Poetry Ribbons | Pine, Plum, and Cherry poetry ribbons, without the full blue set | 5, plus 1 per additional Ribbon |
| Blue Ribbons | Peony, Chrysanthemum, and Maple blue ribbons, without the full red set | 5, plus 1 per additional Ribbon |
| Ribbons | At least five ribbons with neither named set | 1 at five, plus 1 per additional Ribbon |
| Plain Cards | At least ten Plains, counting the sake cup as a Plain as well as an Animal | 1 at ten, plus 1 per additional Plain |
| Flower Viewing | Cherry Curtain and sake cup | 5 |
| Moon Viewing | Pampas Moon and sake cup | 5 |

The two Viewing combinations can both score with the same cup. When banking,
double the subtotal if it is at least **7**, then double it again if the **other
seat** called Koi-Koi. For example, banking 7 against a caller earns 28 for the
winner. The other seat scores nothing; no points are deducted or transferred.
Drawn rounds award neither seat any points.

## Saved matches

The server privately shuffles all six deals and writes the exact orders, current
cards, pending choice, and scores to a version-1 seated checkpoint. Restoring
uses those same orders; a browser cannot submit a deck or seed. The same
checkpoint can record public card references for each seat's latest completed
turn and the hand play for a pending draw choice. These let both seats see the
last plays and flips even when they resolve automatically. Recaps are for
display only; they neither add cards nor determine play or scoring. They start
empty each round. Older saves without a recap still play normally: a pending
draw without a recorded hand play can be taken but contributes no recap.
A bot chooses from its own hand, visible cards, and legal choices only.
Between rounds the server waits for a seat to request the next deal. See the
[mounted API](portal-api.md#koi-koi) for the guest projection and actions.

References for the deck and rule variants: [Nintendo's Koi-Koi rules](https://www.nintendo.com/jp/others/hanafuda_kabufuda/howtoplay/koikoi/index.html),
[Pagat's flower-card overview](https://www.pagat.com/class/flower.html),
[Fuda Wiki's Koi-Koi notes](https://fudawiki.org/en/hanafuda/games/koi-koi), and
[Yamato's English rules and yaku](https://yamato.com/en/hanafuda/how-to-play/).
