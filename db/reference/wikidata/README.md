# Wikidata query results

Data taken from [Wikidata](https://www.wikidata.org), which is dedicated to the public domain under [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/). Each result is committed with its date and the query that made it, so nothing here is fetched when the reference data is built.

| File | What it is |
|---|---|
| `onc-high-priority-interactions.rq` | The query: "significant drug interaction" (P769) statements that cite the ONC high-priority list, with each drug's RxNorm code. |
| `onc-high-priority-interactions-2026-11-19.csv` | Its result on 19 November 2026: one row for each statement, with both drugs' Wikidata IDs, English labels and RxNorm codes. A pair usually appears twice, once from each side. |

The list itself: Phansalkar S, Desai AA, Bell D, et al. High-priority drug-drug interactions for use in electronic health records. *J Am Med Inform Assoc.* 2012;19(5):735-743. doi:10.1136/amiajnl-2011-000612. It was drawn up for the Office of the National Coordinator for Health IT (ONC) as the interactions an electronic record should always warn about.

`tools/reference/import-catalogs.cs` turns the CSV into `db/reference/50-onc-interactions.sql`, keeping the pairs whose two codes are RxNorm ingredients. To refresh it, run the query again, save the result under a new date, and change the file name in the script.
