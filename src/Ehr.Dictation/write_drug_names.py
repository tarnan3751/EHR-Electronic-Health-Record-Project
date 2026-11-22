# /// script
# requires-python = ">=3.12"
# dependencies = []
# ///
"""Writes src/Ehr.Dictation/sidecar/ehr_dictation/drugnames.py: the English drug names the Spanish dictation checks
recognize in a translated proposal (translation.py), so that a drug name in the English must be one the Spanish it
cites says, even one no USAN stem gives away (aspirin, insulin, digoxin).

They come from the RxNorm names in db/reference/10-clinical-codes.sql (RxNorm is published by the NLM without a
licence): every ingredient the chart's allergy list is coded with (its `allergen rxnorm` rows) named in one word, of
four letters or more; every word of those named in several ("glargine" in "insulin glargine"); every word of the
ingredients the medication rows name before their strength ("valproic" in "valproic acid 250 MG Oral Capsule"); and the
first word of each brand name in brackets ("Depakote"). The checks fail closed on a drug name, so a word wrongly here
only discards a right proposal, and a drug name missing here is one the English could add unseen.

But not the words in STOP, which are elements, foods, materials and laboratory tests as often as they are ingredients
("sodium 138", "water", "glucose", "creatinine"), and would otherwise ask a Spanish source for a drug where the
clinician named a result; nor those in WORD_STOP, the words of names given in several words that aren't drug names
themselves: salts and forms ("acid", "hydrochloride", "solution"), foods ("apple", "peanut"), colors, places, animals,
body parts, findings and the infections a vaccine is named for ("hair", "kidney", "rash", "influenza"), laboratory words
("albumin", "protein"), ordinary English ("type" in "adenovirus type 4 vaccine", "daily" in "once-daily
gabapentin"), and Spanish words a transcript says, which would read as a drug in the Spanish too ("ella", "lima").
Botanical and chemical words stay ("arnica", "belladonna"): no translation of a dictation says them unless the Spanish
does. The 4-letter suffix of a biologic's name ("adalimumab-aaty") is left out. WORD_STOP is ours, read word by word
from what the rules above give; a word missing from it is a right proposal discarded, never a wrong one shown.

    uv run src/Ehr.Dictation/write_drug_names.py            # write it
    uv run src/Ehr.Dictation/write_drug_names.py --check    # fail if it is out of date

A sidecar test fails when the file isn't what this writes from the reference data in the repository.
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
SOURCE = REPO_ROOT / "db" / "reference" / "10-clinical-codes.sql"
TARGET = REPO_ROOT / "src" / "Ehr.Dictation" / "sidecar" / "ehr_dictation" / "drugnames.py"
ROW = "allergen\trxnorm\t"
MEDICATION = "medication\trxnorm\t"
QUANTITY = re.compile(r"^(?:\S*\d\S*\s+(?:Release\s+|HR\s+|ML\s+|ACTUAT\s+|MG\s+|DAY\s+|WEEK\s+|MONTH\s+)?)+")

STOP = frozenset(
    """acetone air amber ammonia amylase bentonite beeswax boron bran butane calcium cellulose chlorine chlorophyll
    cholesterol choline cochineal collagen copper creatine creatinine cyclohexane fibrinogen fructose galactose gamboge
    gelatin glucose guaiac helium hemp heptane histamine indigo iodine iron isobutane kaolin lactase lactate lactose
    lanolin lecithin lipase magnesium maltose manganese mannose mica molasses molybdenum nitrogen oxygen paraffin
    pectin perlite petrolatum phenol potassium propane protease prothrombin pumice radium rhamnose rosin samarium
    selenium shellac silicon silver sodium starch strontium styrene sucrose sulfur talc tartrazine thrombin titanium
    trehalose turpentine urea vanadium water zein zinc zirconium""".split()
)
WORD_STOP = frozenset(
    # Salts, elements, chemical groups and forms.
    """acetate acetic acetonide acetyl acetylated acid acids acrylic actuated alcohol alcohols aldehyde alkaline
    alkaloids allergenic amide amine amino ammoniated ammonium anhydride anhydrous aqueous arsenic barium basic
    benzoic benzyl besilate besylate bicarbonate biphasic bitartrate borate boric bromide butyl carbon carbonate
    carboxylate cation cesium chelate chelated chlorate chloride chlorite chromic citrate citric colloidal compound
    concentrate conjugate conjugated cypionate decanoate derivative derived dibasic dihydrate dihydrochloride dimer
    dimesylate dinitrate dioxide disodium dispersion enanthate ester esters ether ethyl ethylsuccinate extract
    ferric ferrous fluoride formula fragments fumarate furoate glycerol glycol glycols hemihydrate hippurate hyclate
    hydride hydro hydrobromide hydrochloric hydrochloride hydrogen hydrogenated hydrolized hydrolyzed hydroxide
    hydroxy hydroxylated inert ingredients insoluble iodide iodinated isolate isomerized isomers lactic lactobionate
    liposomal liposome liquid malate maleate mandelate mesylate methyl methylbromide methylsulfate micro
    microencapsulated micronized microns microspheres mineral mixture mono monobasic monohydrate mononitrate natural
    niacinate nitrate nitric nitrite nitro nitrous oils orotate ortho oxalate oxide pamoate particle peroxide
    phosphate phosphoric picosulfate pigment poly polymer powder preparation preservative probutate propionate
    residues salicylate salicylic soluble solution solvent stabilized stain stearate sulfate sulfonated sulfuric
    synthetic syrup tannate tartrate tetrahydrochloride tincture tribasic tricalcium trisodium undecanoate valerate
    volatile"""
    # Foods and drinks.
    """ almond anise apple apricot asparagus avocado banana barley basil bean beef beet beets berry blackberry
    blueberry broccoli buckwheat butter cabbage cacao cantaloupe carrot casein cashew cauliflower celery cherimoya
    cherry chestnut chia chicken chicory chocolate cider cinnamon clam clove cocoa coconut codfish coffee coriander
    corn crab cranberry cucumber currant duck eggplant fennel fish flaxseed garlic ginger grape grapefruit guava
    hazelnut honey honeydew juice kiwi lamb lambs lemon lentil lettuce lime lingonberry litchi lobster macadamia
    malt mandarin mango melon milk millet mint mints mung mushroom muskmelon nutmeg oatmeal olive onion orange
    oyster papaya parsley peach peanut pear pecan pepper peppermint persimmon pineapple pistachio plum pomegranate
    pork potato pumpkin quinoa radish raspberry rhubarb rice roasted saffron sage salmon salt seaweed sesame shrimp
    skim soybean spearmint spinach sprout strawberry sugar sunflower tabasco tamarind tangerine tapioca thyme
    tomato tuna turkey vanilla vinegar walnut wasabi watermelon wheat wine yeast yolk"""
    # Colors, places and peoples, animals, insects and plants as ordinary words.
    """ algerian alpine american arizona asian atlantic australian bark bavarian bilberry black blue bovine branch
    branches brazil brown bulb bush california canada canis chinese cockroach coral cotton dandelion dander dark
    desert east eastern elderberry english equine european feather flea flower flowering fruit gerbil german goat
    goose grain grass gray green hamster hornet horse indian italian jacket japanese jojoba kentucky korean
    lavender leaf leaves levant light lotus marine maritime meadow mite mongolian mosquito mountain mouse mulberry
    murine needle nordic north northern nylon orchard ovine pearl peel peru peruvian petal pine plains plant pollen
    poppy porcine primrose rabbit ragweed root rose rosemary russian scorpion scot seed senegal shark shea sheep
    shell siberian silk snake southern spanish spores stem sting stinging sudan syrian tallow thorn tick timothy
    tree utah valley venom violet wasp weed west western white willow witch wood wort yellow yellowish york"""
    # Body parts, findings, the infections vaccines are named for, laboratory and clinical words.
    """ acne albumin allergen anthrax antibodies antigen antigens arthritis atrial back blood body bone botulism breath
    burn burning cannabis cartilage cell cells cessation coagulant coagulation covid dengue density dental dermal
    diabetic diphtheria encephalitis enzyme estrogens eyes factor fecal fever fiber fibrillation fibroblast
    fibroblasts fistula follicle germ gland globulin glycoprotein growth hair head helix hematopoietic heme
    hepatitis hormone hypertension hyponatremia immune immunoglobulin influenza inhibitor inner intestinal
    intravenous iris itch kidney lens lipid liver lupus luteinizing lymphocyte marrow measles membrane
    meningococcal mesenchymal mumps nail nerve neurotoxin palm parathyroid pediatric peptide percent pertussis
    pituitary plasma polypeptide polysaccharide polysaccharides progenitor protein pulmonary rabies rash
    respiratory rubella salmonella sars scalp serum skin sleep smallpox smoking stomach strain streptococcus
    subcutaneous subtype subunit tears tetanus thymus thyroid tissue tobacco toxin toxoid triglyceride
    triglycerides typhoid vaccine varicella vessel virus weight zoster"""
    # Ordinary English.
    """ abuse activated active alert align aller alpha anti aqua attenuated augmented baby balm band bedside bell beta
    bitter blink block bloom board boil broad capped care chain char chaste cheat chlor city claw clear coal coast
    comfort common complex component containing cool correct crest critic cross cultivated cultured daily date deep
    delivery delta derm derma deterrent devil direct dock doctor domain domestic doses dosing dragon dram drops
    dust earth ease edible elder emollient engineered enriched envelope evening faced false fast fatty fire
    fisherman flavor formal four fraction free freeze fruiting fungi fusion gall gamma giant graft group guard
    guinea halls hard hazel health healthy heaven heavy high hist holy hops house human husk hyper import imported
    inactivated invert iota jelly john julie kernel kill king kirsty lake levo lido linked little live long loose
    lynn merck management matched matrix maximum medium mixed mole molecular morning multi muse nature next oasis ocean
    once option osmotic paper percy perennial perfect perform petroleum phillips plan polar post poverty prickly
    processed pronto protect quarters queen randomized rating react regular regulatory releasing repeat rind river
    rocky ross rough royal samson scale scrub sensor short shortage simply sloan slow smooth soft sojourn soothe
    sour spike sprinkle star stimulating string substituted substitution summer summers surface sweet tabloid take
    tandem theo thrive time trans treated triple truncated tucks twice type ultra unapproved unspecified value
    vanquish velvet vert vita viva whole wild wing with wool your zone"""
    # Names a Spanish word of a transcript reads as, its accents and ending gone ("coronario" as "coronarium",
    # "forma", "rayos").
    """ alba album alfalfa ambrosia arroyo asiatic atlantica atrac avance azor bahia baza borne cambia canina capsular
    cardiaca caro chloro coco cocos coronarium curcuma dira ella ficus forma gardenia geranium grama hispanica
    impavido integra italica jalapa lancea lenta lima limon magnolia marina medi mitigo montana mori mosco muro
    oregano pedia persa rayos regia robusta rosa salvia silicate sombra sudo tricolor ultramarine utopic vanos
    vernal veronica vinyl""".split()
)


def names(sql: str) -> list[str]:
    found = set()
    for line in sql.splitlines():
        if line.startswith(ROW):
            display = line.split("\t")[3].strip().lower()
            if display.isalpha() and display.isascii():
                found.add(display)
            else:
                found |= _words(display)
        elif line.startswith(MEDICATION):
            display = line.split("\t")[3]
            for brand in re.findall(r"\[([^\]]+)\]", display):
                found |= _words(brand.strip().lower().split()[0])
            # Each ingredient is the words before its strength, after any quantity ("24 HR", "5 ML").
            for part in re.sub(r"\[[^\]]*\]", "", display).split(" / "):
                ingredient = re.match(r"([A-Za-z][^\d]*?)\s+\d", QUANTITY.sub("", part))
                if ingredient:
                    found |= _words(re.sub(r"-[a-z]{4}\b", "", ingredient.group(1).lower()))
    return sorted(name for name in found if len(name) >= 4 and name.isascii() and name not in STOP)


def _words(name: str) -> set[str]:
    return {word for word in re.findall(r"[a-z]+", name) if word not in WORD_STOP}


def module(sql: str) -> str:
    listed = names(sql)
    lines = [
        '"""English drug names the Spanish dictation checks recognize (translation.py): RxNorm ingredients named in one',
        "word, the words of those named in several and of the medication rows' ingredients, and brand names, from",
        "db/reference/10-clinical-codes.sql. Written by src/Ehr.Dictation/write_drug_names.py, which says which words are",
        'left out and why; don\'t edit it."""',
        "",
        "NAMES = frozenset(",
        '    """',
    ]
    line = "   "
    for name in listed:
        if len(line) + 1 + len(name) > 116:
            lines.append(line)
            line = "   "
        line += " " + name
    lines += [line, '    """.split()', ")", ""]
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--check", action="store_true", help="fail if drugnames.py is out of date")
    args = parser.parse_args(argv)
    expected = module(SOURCE.read_text(encoding="utf-8"))
    if args.check:
        if TARGET.read_text(encoding="utf-8") != expected:
            print(f"{TARGET} is out of date: uv run src/Ehr.Dictation/write_drug_names.py", file=sys.stderr)
            return 1
        return 0
    TARGET.write_text(expected, encoding="utf-8", newline="\n")
    print(f"Wrote {len(names(SOURCE.read_text(encoding='utf-8')))} names to {TARGET}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
