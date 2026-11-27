# TC-UC1-06 — Placing an order is keyboard-first (manual part)

The manual part of TC-UC1-06: what needs a real browser. The automated part, checked from the HTML the server renders,
is `TC_UC1_06_Nfr12_KeyboardFirst.cs` in this folder.

| Field | Value |
|---|---|
| Test Case ID | TC-UC1-06 (manual part) |
| Purpose / scenario | A clinician places an order from the chart with the keyboard only: the shortcut opens the form, the cursor starts in the first field, Tab reaches every control in order, and Enter signs the order. |
| Type | Non-functional (usability: keyboard-first) |
| Requirements | NFR-12 |
| Use case | UC 1, Place an order |
| Preconditions | The local stack is running (`infra/compose/README.md`), and the example accounts exist (`dotnet run infra/compose/test-accounts.cs -- setup`). The mouse is not used after step 1. |
| Inputs and system conditions | The on-site app at `https://ehr.example.com:8443`, in a desktop browser. The clinic's server is up. |
| Test data | Clinician `clinician@example.com`, with the example patient (MRN `SYN0000001`) on their panel. Order type Lab, item "Lipid panel", Reason for the test "Synthetic annual check". |
| Expected result | Alt+Shift+O (Option+Shift+O on a Mac) opens the order form. The cursor is in **Item** once a type is chosen. Tab moves through the type links, Item, the details, **Sign order** and **Cancel**, in that order, each with a visible focus outline. Typing "lip" in Item suggests "Lipid panel". Enter in a field signs the order, and the chart shows "Order signed. Lipid panel" and lists it under "Active orders". With a required field empty, the form comes back with the cursor in that field and "Enter the reason for the test." |
| Creator | Tahseen Arnan |

## Steps

1. Sign in as the clinician (password, then the code from `dotnet run infra/compose/test-accounts.cs -- code`), and open the example patient's chart.
2. Press Alt+Shift+O (Option+Shift+O on a Mac). The order form opens.
3. Press Tab until **Lab** has the focus, and press Enter. The cursor is in **Item**.
4. Type `lip`, choose "Lipid panel" from the suggestions with the arrow keys and Enter.
5. Press Tab, and leave **Reason for the test** empty. Press Enter.
6. Read where the cursor is and the message shown.
7. Type `Synthetic annual check` and press Enter.
8. Read the chart.
9. Repeat steps 1 to 8 in each browser below.

## Results

| Browser | Version | Result |
|---|---|---|
| Chrome | not stated | Not Run |
| Edge | not stated | Not Run |
| Firefox | not stated | Not Run |
| WebKit (Safari) | not stated | Not Run |

**Status: Not Run.** This needs a person at a real browser, and the repository has no browser automation. The
automated part of TC-UC1-06 passes; this walkthrough has not been done. Record the browser versions, the date and the
result of each step here when it is.
