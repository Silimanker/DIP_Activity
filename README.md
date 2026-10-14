# Coin Counter 


## How it works (all code is in `CoinAnalyzer.cs`)
1. **Grayscale** (luma weights) and a **3x3 mean blur** to remove speckle.
2. **Otsu's method** picks the threshold automatically; coins become the foreground
   (polarity is detected from the image border, so light-on-dark also works).
3. **Connected-component labeling** (8-connected, iterative flood fill) gives one blob per coin.
4. For each blob: **hole filling** by flood-filling the outside background from the bounding box,
   then **equivalent diameter** = 2*sqrt(filled area / pi).
5. A coin with a **round, centred hole** is a 5-sentimo coin.
6. Diameters are **clustered** into size groups (ratio gap > 1.04), then the groups are matched to
   5c / 10c / 25c / P1 / P5 by a **least-squares fit** of the known size ratios
   (diameter_px = scale x diameter_mm). This is resolution-independent and still works when a
   denomination is missing from the picture.

## Limits
* Coins must not touch each other (the activity says they are evenly spaced). Merged blobs are flagged and ignored.
* Coins cut by the image edge are only guessed.
* The mm values in `Denomination.All` are relative sizes matched to this coin set.


