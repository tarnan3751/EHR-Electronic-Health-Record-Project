# DDInter 2.0 data: license and citation

The file in this folder, `10-drug-interactions.sql`, is derived from **DDInter 2.0** (https://ddinter2.scbdd.com), downloaded on 18 November 2026. It is not covered by this repository's own license.

This project uses DDInter under the **Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International License** (CC BY-NC-SA 4.0, https://creativecommons.org/licenses/by-nc-sa/4.0/), the license recorded for it in the project's decision of 18 November 2026 (`docs/expansion/PLAN.md`). The download page itself carries no license text that the import could read, so the statement should be checked against DDInter's site whenever the data is refreshed. That means:

- **Attribution.** Credit DDInter, as below.
- **Non-commercial.** This data may not be used for commercial purposes. SwiftChart is a course project that uses synthetic data only. If the project ever goes commercial, this table must be replaced.
- **Share-alike.** This derived file is distributed under the same license, CC BY-NC-SA 4.0.

## What was changed

`tools/reference/import-catalogs.cs` made the file from DDInter's eight download files (`ddinter_downloads_code_A.csv` and the others). It kept each pair of drugs and DDInter's level for the pair (major, moderate or minor), replaced each drug's name with its RxNorm ingredient code, and left out pairs whose level DDInter gives as unknown and pairs with a drug that isn't a RxNorm ingredient. Where DDInter lists a pair more than once, the most serious level is kept. Nothing else from DDInter is included.

## Citation

Tian Y, Yi J, Wang N, Wu C, Peng J, Liu S, Yang G, Cao D. DDInter 2.0: an enhanced drug interaction resource with expanded data coverage, new interaction types, and improved user interface. *Nucleic Acids Research*. 2025;53(D1):D1356. https://academic.oup.com/nar/article/53/D1/D1356/7740584

## Not a clinical reference

SwiftChart shows these interactions as decision support in a demonstration system. They are not a complete or current clinical reference, and the absence of a warning doesn't mean two drugs are safe together.
