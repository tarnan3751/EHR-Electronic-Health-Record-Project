"""English drug names the Spanish dictation checks recognize (translation.py): RxNorm ingredients named in one
word, from db/reference/10-clinical-codes.sql. Written by src/Ehr.Dictation/write_drug_names.py, which says
which are left out and why; don't edit it."""

NAMES = frozenset(
    """
    abacavir abaloparatide abatacept abemaciclib abiraterone abrocitinib acalabrutinib acamprosate acarbose
    acebutolol aceclidine aceglutamide acepromazine acesulfame acetaldehyde acetamidoethoxyethanol acetaminophen
    acetazolamide acetoin acetylcarnitine acetylcellulose acetylcholine acetylcysteine acetylglucosamine acitretin
    aclidinium acoltremon acoramidis acrylamide acyclovir adagrasib adalimumab adapalene adefovir adenosine
    aducanumab afamelanotide afatinib aficamten aflibercept agar alanine alanylglutamine albendazole albuterol
    alcaftadine alclometasone alcloxa aldesleukin aldioxa alectinib alemtuzumab alendronate alfentanil alfuzosin
    alirocumab aliskiren alitretinoin allantoin allopurinol almotriptan alogliptin alosetron alpelisib alprazolam
    alprostadil alteplase altrenogest aluminum alvimopan amantadine ambrisentan ambroxol amcinonide amdinocillin
    amifampridine amifostine amikacin amiloride aminolevulinate aminometradine aminopentamide aminophylline
    aminopromazine amiodarone amisulpride amitriptyline amivantamab amlexanox amlodipine amobarbital amoproxan
    amoxapine amoxicillin amphetamine ampicillin amprenavir amylopectin anacaulase anagrelide anakinra anastrozole
    anhydroxylitol anidulafungin anifrolumab ansuvimab anthralin apaflurane apalutamide apitegromab apixaban
    apomorphine apraclonidine apremilast aprepitant aprocitentan aprotinin arbutin arformoterol argatroban arginine
    argipressin arimoclomol aripiprazole armodafinil artemether artesunate articaine asciminib asenapine
    asiaticoside asparaginase asparagine aspartame aspartate aspirin astaxanthin atacicept atazanavir atenolol
    atezolizumab atipamezole atogepant atoltivimab atomoxetine atorvastatin atovaquone atracurium atrasentan
    atropine auranofin avacopan avanafil avapritinib avatrombopag avelumab avibactam avobenzone avutometinib
    axatilimab axitinib azacitidine azathioprine azelastine azelate azilsartan azithromycin aztreonam azulene
    bacitracin baclofen baicalin bakuchiol baloxavir balsalazide baricitinib basiliximab baxdrostat bazedoxifene
    becaplermin beclomethasone bedaquiline belatacept belimumab belinostat belumosudil belzutifan benazepril
    bendamustine benoxinate benphothiamine benralizumab bentoquatam benzaldehyde benzalkonium benzethonium
    benzgalantamine benzhydrocodone benznidazole benzoate benzocaine benzonatate benzophenone benzphetamine
    benztropine benzyltrimethylsilane bepotastine beractant berdazimer berotralstat besifloxacin betadex betaine
    betamethasone betasizofiran betaxolol bethanechol betulin bevacizumab bexagliflozin bexarotene bezafibrate
    bezlotoxumab bicalutamide bicisate bictegravir bimatoprost bimekizumab binimetinib biotin bisabolol bisacodyl
    bismuth bisoprolol bivalirudin bleomycin blinatumomab borneol bortezomib bosentan bosutinib bremelanotide
    brensocatib brepocitinib bretylium brexanolone brexpiprazole brigatinib brimonidine brincidofovir brinzolamide
    brivaracetam brodalumab brolucizumab bromelains bromfenac bromhexine bromocriptine bromotheophylline
    brompheniramine bronopol budesonide bulevirtide bumetanide bupivacaine buprenorphine bupropion burosumab
    buserelin buspirone busulfan butafosfan butalbital butamben butenafine butoconazole butorphanol butyloctanol
    butylparaben butylscopolamine cabazitaxel cabergoline cabotegravir cabozantinib caffeine calamine calcifediol
    calcipotriene calcitriol calcobutrol calfactant camizestrant camphor canagliflozin canakinumab candesartan
    cangrelor cannabidiol cantharidin capecitabine capivasertib caplacizumab capmatinib caprolactone capsaicin
    captopril carbachol carbamazepine carbamoylcholine carbazochrome carbetapentane carbidopa carbimazole
    carbinoxamine carbocysteine carbomer carboplatin carboprost carboxymethylcellulose carboxypolymethylene
    carfilzomib cariprazine carisoprodol carmoisine carmustine carnitine carnosine carprofen carrageenan carteolol
    carvedilol caryophyllene casimersen caspofungin cedazuridine cedrol cefaclor cefadroxil cefazolin cefdinir
    cefepime cefiderocol cefixime cefotaxime cefotetan cefoxitin cefpodoxime cefprozil ceftaroline ceftazidime
    ceftiofur ceftobiprole ceftolozane ceftriaxone cefuroxime celecoxib cemiplimab cenegermin cenobamate cephalexin
    ceresin ceritinib cerliponase cetirizine cetrorelix cetuximab cetylpyridinium cevimeline chenodeoxycholate
    chlophedianol chlorambucil chloramphenicol chlorcyclizine chlordiazepoxide chlorhexidine chlorobutanol
    chlorocresol chloroform chloroprocaine chloroquine chlorotheophylline chlorothiazide chlorothymol chloroxylenol
    chlorphenesin chlorpheniramine chlorpromazine chlorthalidone chlorzoxazone cholecalciferol chrysin chymotrypsin
    ciclesonide ciclopirox cidofovir cilastatin cilostazol cimetidine cinacalcet ciprofloxacin cisapride
    cisatracurium cisplatin citalopram citicoline citral citrulline cladribine clarithromycin clascoterone
    clavulanate clemastine clesrovimab clevidipine clidinium climbazole clindamycin clobazam clobetasol clocortolone
    clofarabine clofazimine clomiphene clomipramine clonazepam clonidine clopidogrel clorazepate clotrimazole
    clozapine cobamamide cobicistat cobimetinib cocaine cocamide codeine colchicine colesevelam colestipol colistin
    collagenase concizumab condoliase conivaptan copanlisib cornsilk corticotropin cortisone cosibelimab cosyntropin
    coumarin creosol creosote cresol crinecerfont crisaborole crizanlizumab crizotinib crofelemer cromolyn
    croscarmellose crospovidone crotamiton crovalimab curcumin curdlan cyclobenzaprine cyclodextrins cyclododecane
    cyclomethicone cyclopentanone cyclopentolate cyclophosphamide cycloserine cyclosporine cyproheptadine cysteamine
    cysteine cytarabine dabigatran dabrafenib dacarbazine dacomitinib dactinomycin dalbavancin dalfampridine
    dalteparin danazol danicopan dantrolene dapagliflozin dapiprazole daprodustat dapsone daptomycin daratumumab
    daraxonrasib daridorexant darifenacin darolutamide darunavir dasatinib dasiglucagon daunorubicin
    deacetylbisacodyl deanol decamethyltetrasiloxane decitabine decoquinate decyloxazolidinone defactinib
    deferasirox deferiprone deferoxamine defibrotide deflazacort degarelix delafloxacin delgocitinib demecarium
    demeclocycline denosumab deoxycholate depemokimab deracoxib desflurane desipramine desirudin desloratadine
    deslorelin desmopressin desogestrel desonide desoximetasone desvenlafaxine detomidine deucravacitinib
    deuruxolitinib deutetrabenazine deutivacaftor dexamethasone dexbrompheniramine dexchlorpheniramine
    dexlansoprazole dexmedetomidine dexmethylphenidate dexpanthenol dexrazoxane dextrates dextroamphetamine
    dextromethorphan diazepam diazolidinylurea diazoxide dibucaine dicetylphosphate dichlorphenamide diclazuril
    diclofenac dicloxacillin dicyclomine didanosine dienestrol dienogest diethanolamine diethylpropion
    diethylstilbestrol difamilast difelikefalin difenoxin diflorasone diflunisal difluprednate diglycerin digoxin
    dihydrocodeine dihydroergotamine dihydroeugenol dihydromyrcenol dihydroxyacetone dihydroxypropyltrimonium
    diisopropanolamine diisopropylamine diltiazem dimenhydrinate dimercaprol dimethicone dimethylacetamide
    dinoprostone dinutuximab diosmin dioxane diphenhydramine diphenoxylate dipyridamole dipyrone disopyramide
    disulfiram dobutamine docetaxel doconexent docosahexaenoate docosanol docusate dodecanol dodecylphosphocholine
    dofetilide dolasetron dolutegravir donanemab donepezil donidalorsen donislecel dopamine doravirine dordaviprone
    dorzolamide dostarlimab dotatate doxapram doxazosin doxecitine doxepin doxercalciferol doxorubicin doxribtimine
    doxycycline doxylamine dronabinol dronedarone droperidol drospirenone droxidopa dulaglutide duloxetine dupilumab
    durlobactam durvalumab dutasteride duvelisib dyclonine ebanol ecallantide econazole ecothiopate ectoine
    eculizumab edaravone edetol edoxaban efavirenz efinaconazole eflapegrastim eflornithine eicosapentaenoate
    elacestrant elafibranor elagolix elamipretide elapegademase elbasvir eletriptan elexacaftor eliglustat
    elinzanetant elotuzumab elranatamab eltrombopag eluxadoline elvitegravir emapalumab emicizumab empagliflozin
    emtricitabine enalapril enalaprilat enasidenib encorafenib enfortumab enfuvirtide enlicitide enmetazobactam
    enoxaparin enrofloxacin ensartinib ensifentrine ensitrelvir entacapone entecavir entrectinib enzalutamide
    epcoritamab ephedrine epinastine epinephrine epirubicin eplerenone eplontersen epoprostenol eptifibatide
    eptinezumab eravacycline erdafitinib erenumab ergocalciferol ergotamine ergothioneine eribulin erlotinib
    ertapenem ertugliflozin erythritol erythromycin erythrosine escin escitalopram esketamine eslicarbazepine
    esmolol esomeprazole estazolam estetrol estradiol estramustine estriol estropipate eszopiclone etanercept
    etelcalcetide eteplirsen ethacrynate ethambutol ethamsylate ethanol ethanolamine ethionamide ethosuximide
    ethylenediamine ethylhexylglycerin ethynodiol etodolac etomidate etonogestrel etoposide etrasimod etravirine
    etripamil eucalyptol eugenol everolimus evinacumab evolocumab exemestane exenatide ezetimibe famciclovir
    famotidine faricimab farnesol febantel febuxostat fedratinib felbamate felodipine fenbendazole fenfluramine
    fenofibrate fenoprofen fentanyl ferumoxytol fesoterodine fexinidazole fexofenadine fezolinetant fidaxomicin
    filgrastim finafloxacin finasteride finerenone fingolimod fitusiran flavone flavoxate flecainide flibanserin
    floralozone florfenicol floxuridine fluconazole flucytosine fludarabine fludrocortisone flumazenil flunisolide
    flunixin fluocinolone fluocinonide fluorescein fluorometholone fluorouracil fluoxetine fluphenazine
    flurandrenolide flurazepam flurbiprofen flutamide fluticasone fluvastatin fluvoxamine fomepizole fondaparinux
    formaldehyde formoterol fosamprenavir fosaprepitant foscarnet fosdenopterin fosfomycin fosinopril fosinoprilat
    fosphenytoin fostamatinib fostemsavir fremanezumab frovatriptan fructooligosaccharide fruquintinib fulvestrant
    furazolidone furcelleran furosemide futibatinib gabapentin gadobutrol gadodiamide gadopiclenol gadoquatrane
    gadoteridol galactoarabinan galantamine galcanezumab galsulfase ganaxolone ganciclovir ganirelix garadacimab
    garetosmab gatifloxacin gedatolisib gefitinib gemcitabine gemfibrozil genistein gentamicin gepirone gepotidacin
    geraniol gilteritinib givinostat givosiran glabridin glasdegib glatiramer glecaprevir glimepiride glipizide
    glofitamab glucagon glucarpidase glucomannan gluconolactone glucosamine glutamate glutamine glutaral glutathione
    glyburide glycerin glycine glycolate glycopyrronium glycylglycine glyoxal golimumab golodirsen goserelin
    gozetotide gramicidin granisetron grazoprevir griseofulvin guaiacol guaifenesin guanabenz guanfacine guselkumab
    halcinonide halobetasol haloperidol hectorite hemin heparin hesperetin hesperidin hetastarch hexachlorophene
    hexadecane hexamethyldisiloxane hexamethylindanopyran hexanediol hexyldecanol hexylresorcinol histidine
    histrelin homatropine hyaluronate hyaluronidase hydantoin hydralazine hydrochlorothiazide hydrocodone
    hydrocortisone hydromorphone hydroquinone hydrotalcite hydroxocobalamin hydroxychloroquine hydroxycitronellal
    hydroxyproline hydroxypropylcellulose hydroxytyrosol hydroxyurea hydroxyzine hyoscyamine hypochlorite
    hypromellose ibalizumab ibandronate iberdomide ibrexafungerp ibrutinib ibuprofen ibutamoren ibutilide icatibant
    ichthammol icodextrin icotrokinra idarubicin idarucizumab idebenone idelalisib idoxuridine idursulfase
    ifosfamide iloperidone iloprost imatinib imetelstat imidocarb imidurea imiglucerase imipenem imipramine
    imiquimod imlunestrant inavolisib inclisiran indapamide indomethacin inebilizumab infigratinib infliximab
    inositol inotersen inulin iodixanol iodoquinol iohexol iomeprol iopamidol iopromide ioversol ipilimumab
    ipratropium iptacopan irbesartan irinotecan isatuximab isavuconazole isavuconazonium islatravir isobutylene
    isobutylparaben isocarboxazid isododecane isoflurane isoleucine isomalt isoniazid isopentyldiol isoprene
    isopropanolamine isoproterenol isosorbide isotretinoin isoxsuprine isradipine istradefylline itraconazole
    ivabradine ivacaftor ivermectin ivosidenib ixabepilone ixazomib ixekizumab jasmone kanamycin ketamine
    ketoconazole ketoprofen ketorolac ketotifen labetalol lacosamide lactitol lactoperoxidase lactulose lamivudine
    lamotrigine lanadelumab landiolol lanreotide lansoprazole lapatinib laponite laronidase larotrectinib lasmiditan
    latanoprost lauramide laurocapram lazertinib lebrikizumab lecanemab ledipasvir lefamulin leflunomide lemborexant
    lenacapavir lenalidomide leniolisib lenvatinib lerodalcibep lesinurad letermovir letrozole leucine leucovorin
    leuprolide levacetylleucine levalbuterol levamisole levamlodipine levetiracetam levobunolol levocarnitine
    levocetirizine levodopa levofloxacin levoglucose levoisomethadone levoleucovorin levomefolate levomenthol
    levomilnacipran levonordefrin levonorgestrel levorphanol levothyroxine licorice lidocaine lifileucel lifitegrast
    linaclotide linagliptin lincomycin lindane linerixibat linezolid linoleate linvoseltamab linzagolix liothyronine
    liraglutide lisdexamfetamine lisinopril lithium lixisenatide lodoxamide lofexidine lomitapide lomustine
    lonafarnib lonapegsomatropin loperamide lopinavir loratadine lorazepam lorlatinib losartan loteprednol lotilaner
    lovastatin loxapine lubiprostone lufenuron luliconazole lumacaftor lumasiran lumateperone lumefantrine
    luprostiol lurasidone lurbinectedin luspatercept lusutrombopag lutein lycopene lysine lysozyme lysyllysllysine
    macimorelin macitentan mafenide maftivimab magaldrate malathion malondialdehyde maltitol maltodextrin maltol
    mannitol maralixibat maraviroc marbofloxacin margetuximab maribavir marstacimab masoprocol mavacamten
    mavorixafor mebendazole mebrofenin mecamylamine mecasermin mechlorethamine meclizine meclofenamate mecobalamin
    medroxyprogesterone mefenamate mefloquine megestrol meglumine melatonin melengestrol meloxicam melphalan
    memantine menatetrenone menthol meperidine mepivacaine mepolizumab meprobamate merbromin mercaptopurine
    meropenem mesalamine mesna metacresol metaproterenol metaxalone metformin methacholine methadone methamphetamine
    methanol methazolamide methenamine methimazole methionine methocarbamol methohexital methotrexate methoxsalen
    methoxyisopropanol methscopolamine methsuximide methylcellulose methyldopa methylergonovine methylhomatropine
    methylmethacrylate methylnaltrexone methylnicotinate methylparaben methylphenidate methylprednisolone
    methylpropanediol methylsulfonylmethane methyltestosterone metoclopramide metolazone metoprolol metreleptin
    metronidazole metyrapone metyrosine mexiletine micafungin miconazole midazolam midodrine midostaurin
    mifepristone migalastat miglitol miglustat milnacipran milrinone milsaperidone miltefosine minocycline minoxidil
    mipomersen mirabegron mirdametinib mirikizumab mirtazapine misoprostol mitapivat mitomycin mitotane mitoxantrone
    mobocertinib modafinil moexipril moexiprilat mogamulizumab molindone molnupiravir momelotinib mometasone
    monofluorophosphate monoolein montelukast montmorrillonite morphine mosunetuzumab motixafortide moxidectin
    moxifloxacin mupirocin myrcene nabilone nabumetone nadolol nafarelin nafcillin naftifine nalbuphine naldemedine
    nalmefene naloxegol naloxone naltrexone naphazoline naphtha naproxen naratriptan narsoplimab natalizumab
    natamycin nateglinide nattokinase navepegritide naxitamab nebivolol necitumumab nedocromil nedosiran nefazodone
    nelarabine nelfinavir nemolizumab neomycin neostigmine neotame nepafenac neral nerandomilast neratinib nerol
    netarsudil netupitant nevirapine niacin niacinamide nicardipine nicotine nifedipine nifurtimox nilotinib
    nilutamide nimodipine nintedanib nipocalimab niraparib nirmatrelvir nirogacestat nirsevimab nisoldipine
    nitazoxanide nitenpyram nitisinone nitrocellulose nitrofurantoin nitroglycerin nitroprusside nivolumab
    nizatidine nonivamide norelgestromin norepinephrine norethindrone norflurane norgestimate norgestrel
    nortriptyline nusinersen nystatin obiltoxaximab obinutuzumab ocrelizumab octinoxate octisalate octreotide
    octyltriethoxysilane odesivimab odevixibat ofatumumab ofloxacin olanzapine olaparib oleuropein olezarsen
    oliceridine olmesartan olodaterol olopatadine olsalazine olutasidenib omadacycline omalizumab omaveloxolone
    omeprazole omidenepag omidubicel ondansetron opicapone opium oprelvekin orforglipron oritavancin orlistat
    orphenadrine oseltamivir osilodrostat osimertinib ospemifene oteseconazole oxaceprol oxacillin oxaliplatin
    oxandrolone oxaprozin oxazepam oxcarbazepine oxibendazole oxiconazole oxybate oxybenzone oxybutynin
    oxychlorosene oxycodone oxymetazoline oxymorphone oxyquinoline oxytocin ozanimod ozenoxacin paclitaxel
    pacritinib pafolacianine palbociclib palifermin paliperidone palivizumab palonosetron palovarotene paltusotine
    pamabrom pamidronate pancreatin pancuronium panitumumab panobinostat pantethine panthenol pantolactone
    pantoprazole pantothenate papain papaverine paricalcitol paromomycin paroxetine pasireotide patiromer patisiran
    pazopanib pegaspargase pegcetacoplan pegfilgrastim pegloticase pegvaliase pegvisomant pegzilarginase
    pembrolizumab pemetrexed pemigatinib pemirolast pemivibart penciclovir penicillamine penpulimab pentadecalactone
    pentaerythritol pentamidine pentazocine pentobarbital pentostatin pentoxifylline peramivir perampanel
    perflunafene perfluorohexyloctane perflutren perindopril perindoprilat permethrin perphenazine pertuzumab
    pexidartinib phenazopyridine phendimetrazine phenelzine pheniramine phenobarbital phenolsulfonphthalein
    phenoxybenzamine phenoxyethanol phentermine phentolamine phenylacetate phenylalanine phenylbutazone
    phenylbutyrate phenylephrine phenylpropanol phenyltoloxamine phenytoin phloretin physostigmine phytosphingosine
    phytosterols pibrentasvir picosulfurate pilocarpine pimavanserin pimecrolimus pimobendan pimozide pindolol
    pinene pioglitazone piperacillin piperazine piperine piperonal pirfenidone piroxicam pirtobrutinib pitavastatin
    pitolisant plasminogen plazomicin plecanatide plerixafor plozasiran podofilox podophyllin polacrilin policosanol
    polidocanol polihexanide polistirex poloxalene polydextrose polytetrafluoroethylene pomalidomide ponatinib
    ponazuril ponesimod posaconazole povidone pozelimab pralatrexate pralidoxime pralsetinib pramipexole pramlintide
    pramoxine prasterone prasugrel pravastatin praziquantel prazosin prednicarbate prednisolone prednisone
    pregabalin pretomanid prezatide prilocaine primaquine primidone probenecid procainamide procaine procarbazine
    prochlorperazine proflavine progesterone proguanil proline promethazine propafenone propantheline proparacaine
    propofol propolis propranolol propylene propylhexedrine propylparaben propylthiouracil protamines protriptyline
    prucalopride pseudoephedrine psyllium pullulan pyrantel pyrazinamide pyrethrins pyridostigmine pyridoxine
    pyrilamine pyrimethamine pyrithione pyrogallol pyruvate quadrosilan quazepam quercetin quetiapine quinapril
    quinaprilat quinidine quinine quizartinib rabeprazole racementhol racepinephrine raloxifene raltegravir
    ramelteon ramipril ramiprilat ramucirumab ranibizumab ranitidine ranolazine rasagiline rasburicase ravulizumab
    raxibacumab regorafenib relacorilant relatlimab relebactam relugolix remdesivir remibrutinib remifentanil
    remimazolam repaglinide repotrectinib reserpine reslizumab resmetirom resorcinol resveratrol retapamulin
    reteplase retifanlimab retinaldehyde revefenacin revumenib rezafungin ribavirin ribociclib riboflavin rifabutin
    rifampin rifapentine rifaximin rilonacept rilpivirine riluzole rilzabrutinib rimantadine rimegepant riociguat
    ripretinib risankizumab risdiplam risedronate risperidone ritlecitinib ritonavir rituximab rivaroxaban
    rivastigmine rizatriptan robenacoxib rocuronium roflumilast rolapitant romidepsin romifidine romiplostim
    romosozumab ronidazole ropinirole ropivacaine rosiglitazone rosuvastatin rotigotine rozanolixizumab rucaparib
    rufinamide rusfertide rutin ruxolitinib saccharin sacituzumab sacrosidase sacubitril sacubitrilat safinamide
    salicylamide salmeterol salsalate samidorphan sapanisertib sapropterin sarcosine sarecycline sargramostim
    sarilumab satralizumab saxagliptin sclareolide scopolamine sebetralstat secnidazole secukinumab segesterone
    seladelpar selegiline selenite selenomethionine selexipag selinexor selpercatinib selumetinib semaglutide
    sepiapterin serdexmethylphenidate serine serratiopeptidase sertaconazole sertraline setmelanotide sevabertinib
    sevelamer sevoflurane sibeprenlimab silanetriol sildenafil silodosin silodrate siltuximab simethicone
    simvastatin sincalide sinecatechins siponimod sirolimus sitagliptin sofosbuvir sofpironium solifenacin
    solriamfetol somapacitan somatrogon somatropin sonidegib sonrotoclax sorafenib sorbitan sorbitol sotagliflozin
    sotalol sotatercept sotorasib sparsentan spesolimab spinosad spironolactone spirulina squalane squalene
    stavudine steviol stevioside stiripentol streptomycin streptozocin succimer succinate succinylcholine sucralfate
    sucralose sufentanil sugammadex sulbactam sulbutiamine sulconazole sulfacetamide sulfachlorpyridazine
    sulfadiazine sulfadimethoxine sulfamethoxazole sulfasalazine sulfathiazole sulindac sulisobenzone sulopenem
    sulpiride sumatriptan sunitinib sunvozertinib sutimlimab suvorexant suzetrigine tacrolimus tadalafil tafamidis
    tafasitamab tafenoquine tafluprost tagraxofusp talazoparib taletrectinib talquetamab tamoxifen tamsulosin
    tapentadol tapinarof tarlatamab tasimelteon taurine taurolidine tavaborole tavapadon taxifolin tazarotene
    tazemetostat tazobactam tebentafusp teclistamab tecovirimat tedizolid teduglutide teferrol tegaserod telavancin
    telisotuzumab telmisartan telotristat temazepam temozolomide temsavir temsirolimus tenapanor tenecteplase
    teplizumab tepotinib teprotumumab terazosin terbinafine terbutaline terconazole teriflunomide teriparatide
    terlipressin terpineol terpinolene tesamorelin testosterone tetrabenazine tetracaine tetracycline
    tetrahydrodiferuloylmethane tetrahydrolinalool tetrahydrozoline tetraxetan tezacaftor tezepelumab thalidomide
    theanine theophylline thiamine thimerosal thioctate thioglycerol thioguanine thioridazine thiosulfate thiotepa
    thiothixene thonzonium thonzylamine threonine thymol tiagabine ticagrelor tigecycline tildrakizumab timolol
    tinidazole tioconazole tiopronin tiotropium tipiracil tipranavir tirbanibulin tirofiban tirzepatide
    tisagenlecleucel tislelizumab tisotumab tivozanib tizanidine tobramycin toceranib tocilizumab tocopherol
    tocophersolan tofacitinib tofersen tolazoline tolcapone tolmetin tolnaftate tolterodine toltrazuril tolvaptan
    topiramate topotecan toremifene toripalimab torsemide totarol tovorafenib trabectedin tradipitant tragacanth
    tralokinumab tramadol trametinib trandolapril trandolaprilat tranylcypromine trastuzumab travoprost trazodone
    tremelimumab treosulfan treprostinil tretinoin triacetin triamcinolone triamterene triazolam tribehenin tricaine
    tricaprin tricaprylin tricetin trichlormethiazide trichloroacetate triclabendazole triclosan tridecane trientine
    triethanolamine triethylamine triethylhexanoin trifarotene trifluoperazine trifluridine triheptanoin
    trihexyphenidyl trihydroxystearin triisopropanolamine triisostearin trilaciclib trilostane trimethobenzamide
    trimethoprim trimipramine trimyristin triolein tripalmitin tripelennamine triprolidine triptorelin trisiloxane
    tristearin trofinetide tromethamine tropicamide tropolone trospium tryptophan tucatinib tulathromycin tylosin
    tyloxapol tyrosine ubidecarenone ubiquinol ublituximab ubrogepant ulipristal umeclidinium umirolimus undecane
    undecylenate upadacitinib uridine ursadiol ursodeoxycholate ustekinumab vaborbactam vadadustat valacyclovir
    valbenazine valganciclovir valine valproate valrubicin valsartan vamorolone vancomycin vandetanib vanillin
    vanzacaftor vardenafil varenicline vasicinone vecuronium vedolizumab veligrotug velpatasvir vemurafenib
    venetoclax venlafaxine vepdegestrant verapamil vericiguat verteporfin vibegron vidarabine vigabatrin vilanterol
    vilazodone vilobelimab viloxazine viltolarsen vimseltinib vinblastine vincristine vinorelbine viridiflorol
    vismodegib voclosporin volanesorsen vonoprazan vorapaxar vorasidenib voriconazole vorinostat vortioxetine
    vosoritide voxilaprevir vutrisiran warfarin xanomeline xylazine xylitol xylitylglucoside xylometazoline
    yohimbine zafirlukast zaleplon zanamivir zanidatamab zanubrutinib zavegepant zeaxanthin zenocutuzumab ziconotide
    zidebactam zidesamtinib zidovudine ziftomenib zileuton zilganersen zilucoplan zilurgisertib ziprasidone
    zolbetuximab zoliflodacin zolmitriptan zolpidem zongertinib zonisamide zuranolone
    """.split()
)
