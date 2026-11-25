# Big Two

Play one unranked hand with four humans, or with one human and three bots. The server
shuffles one 52-card deck and deals 13 cards to each seat. The first player to empty
their hand wins immediately. There are no wagers, chips, ratings, or cumulative scores.

## Turns

- The holder of the **three of diamonds** leads, using it alone or in a legal combination.
  Play advances in seat order, wrapping from seat 3 to seat 0.
- A lead may be any valid combination. To follow, play the same number of cards and
  beat the current play, or pass even if you have a legal play. You cannot pass on a lead.
- Passing doesn't take you out of the trick. If someone beats the play, all seats can
  respond again in turn. After three consecutive passes, clear the trick and let the
  last player who put down cards lead any valid combination.

## Combinations

Ranks ascend **3, 4, …, 10, J, Q, K, A, 2**. Suits ascend **diamonds, clubs, hearts, spades**.
A single is any card; pairs and triples share a rank. These are compared by their
highest card, rank first then suit.

Five-card plays rank from weakest to strongest:

| Combination | Cards and comparison within the category |
| --- | --- |
| Straight | Five consecutive ranks from 3–A. Compare the highest card by rank then suit. No 2s or wraps. |
| Flush | Five cards of one suit. Compare from highest card downward, rank first then suit. |
| Full house | A triple and a pair. Compare the rank of the triple. |
| Four of a kind | Four cards of one rank and any fifth card. Compare the rank of the four. |
| Straight flush | A straight of one suit, with the same rank and suit comparison as a straight. |

A play must be strictly higher; identical strength does not beat the current play.
Bots use only their own cards and the public table position. They play a largest
available legal combination and pass only when they cannot play.
