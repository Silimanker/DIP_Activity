# Philippine Coin Counter (C# WinForms, .NET 10, no third-party libraries)

Open `CoinCounter.sln` in Visual Studio, press F5, click **Open image...** and pick the coin photo
(`SampleImages/coins_sample.png` is the activity picture, cropped from a screenshot - use the original for best results).

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

Expected result on the activity image: 7 x 5c, 11 x 10c, 28 x 25c, 13 x P1, 5 x P5 = **P46.45**
(64 coins).

## Limits
* Coins must not touch each other (the activity says they are evenly spaced). Merged blobs are flagged and ignored.
* Coins cut by the image edge are only guessed.
* The mm values in `Denomination.All` are relative sizes matched to this coin set.

## Making the .exe to share (no zip)
Visual Studio: Build > Publish > Folder, then set "Deployment mode: Self-contained, Target runtime win-x64,
Produce single file". Or from a terminal in the project folder:

    dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true

Upload the single `CoinCounter.exe` from `bin/Release/net10.0-windows/win-x64/publish/` to OneDrive/GitHub and share that link.
