from __future__ import annotations

from dataclasses import dataclass

# ⚠ Traductions FR rédigées à la main pour ce test (`bench/harness_evaluate.py`) — pas des
# traductions certifiées ni publiées, juste une base de comparaison pour repérer les
# régressions visuellement. Le `source_text` est le texte ORIGINAL connu (livre/passage
# standard, domaine public), pas une retranscription littérale de la sortie WLK (qui a ses
# propres erreurs STT, cf. les runs réels déjà observés) : le diff capture donc à la fois la
# fidélité STT et la qualité de traduction, pas la traduction seule. Les fichiers LibriVox
# commencent en général par une annonce du lecteur ("LibriVox.org...") avant le texte
# littéraire lui-même (constaté sur le corpus `a`) — pas incluse ici, à prendre en compte
# dans le diff.


@dataclass(frozen=True)
class ReferenceTranscript:
    key: str
    source_text: str
    fr_reference: str
    provenance: str


_ALICE_SOURCE = (
    "Alice was beginning to get very tired of sitting by her sister on the bank and of "
    "having nothing to do. Once or twice she peeped into the book her sister was reading, "
    "but it had no pictures or conversations in it. And what's the use of a book, thought "
    "Alice, without pictures or conversation? So she was considering in her own mind, as "
    "well as she could, for the hot day made her feel very sleepy and stupid, whether the "
    "pleasure of making a daisy chain would be worth the trouble of getting up and picking "
    "the daisies, when suddenly a white rabbit with pink eyes ran close by her. There was "
    "nothing so very remarkable in that; nor did Alice think it so very much out of the way "
    'to hear the rabbit say to itself, "Oh dear! Oh dear! I shall be late!" When she thought '
    "it over afterwards, it occurred to her that she ought to have wondered at this, but at "
    "the time it all seemed quite natural. But when the rabbit actually took a watch out of "
    "its waistcoat pocket, and looked at it, and then hurried on, Alice started to her feet, "
    "for it flashed across her mind that she had never before seen a rabbit with either a "
    "waistcoat pocket or a watch to take out of it, and burning with curiosity, she ran "
    "across the field after it, and fortunately was just in time to see it pop down a large "
    "rabbit hole under the hedge. In another moment down went Alice after it, never once "
    "considering how in the world she was to get out again. The rabbit hole went straight "
    "on like a tunnel for some way, and then dipped suddenly down, so suddenly that Alice "
    "had not a moment to think about stopping herself before she found herself falling down "
    "a very deep well. Either the well was very deep, or she fell very slowly, for she had "
    "plenty of time as she went down to look about her and to wonder what was going to "
    "happen next. First, she tried to look down and make out what she was coming to, but it "
    "was too dark to see anything; then she looked at the sides of the well, and noticed "
    "that they were filled with cupboards and bookshelves; here and there she saw maps and "
    "pictures hung upon pegs. She took down a jar from one of the shelves as she passed; it "
    "was labelled ORANGE MARMALADE, but to her great disappointment it was empty. She did "
    "not like to drop the jar for fear of killing somebody underneath, so managed to put it "
    "into one of the cupboards as she fell past it. Well! thought Alice to herself, after "
    "such a fall as this, I shall think nothing of tumbling down stairs! How brave they'll "
    "all think me at home! Why, I wouldn't say anything about it, even if I fell off the top "
    "of the house! (Which was very likely true.) Down, down, down. Would the fall never come "
    "to an end? I wonder how many miles I've fallen by this time? she said aloud. I must be "
    "getting somewhere near the centre of the earth. Let me see: that would be four thousand "
    "miles down, I think. (For, you see, Alice had learnt several things of this sort in her "
    "lessons in the schoolroom, and though this was not a very good opportunity for showing "
    "off her knowledge, as there was no one to listen to her, still it was good practice to "
    "say it over.) Yes, that's about the right distance, but then I wonder what latitude or "
    "longitude I've got to?"
)

_ALICE_FR = (
    "Alice commençait à se sentir très lasse de rester assise à côté de sa sœur sur la "
    "berge, sans rien à faire. Une fois ou deux, elle avait jeté un coup d'œil dans le livre "
    "que lisait sa sœur, mais il n'y avait ni images ni dialogues. « Et à quoi peut bien "
    "servir un livre, pensa Alice, sans images ni dialogues ? » Elle se demandait donc, du "
    "mieux qu'elle pouvait — car la chaleur du jour la rendait toute somnolente et un peu "
    "sotte — si le plaisir de faire une chaîne de pâquerettes valait la peine de se lever "
    "pour aller les cueillir, quand soudain un lapin blanc aux yeux roses passa en courant "
    "tout près d'elle. Il n'y avait rien de bien extraordinaire à cela ; Alice ne trouva pas "
    "non plus tellement étrange d'entendre le lapin se dire à lui-même : « Oh là là ! Oh là "
    "là ! Je vais être en retard ! » Quand elle y repensa plus tard, il lui vint à l'esprit "
    "qu'elle aurait dû s'en étonner, mais sur le moment tout cela lui avait paru tout à fait "
    "naturel. Mais quand le lapin sortit effectivement une montre de la poche de son gilet, "
    "la regarda, puis se hâta, Alice se leva d'un bond, car l'idée lui traversa l'esprit "
    "qu'elle n'avait jamais vu de lapin ayant à la fois une poche de gilet et une montre à en "
    "sortir ; et, brûlant de curiosité, elle traversa le champ en courant après lui, et eut "
    "la chance d'arriver juste à temps pour le voir disparaître dans un grand terrier sous la "
    "haie. L'instant d'après, Alice s'y engouffra à son tour, sans jamais se demander "
    "comment, au juste, elle allait pouvoir en ressortir. Le terrier allait tout droit, comme "
    "un tunnel, sur une certaine distance, puis plongeait brusquement — si brusquement "
    "qu'Alice n'eut pas le temps de songer à s'arrêter avant de se retrouver en train de "
    "tomber dans un puits très profond. Soit le puits était très profond, soit elle tombait "
    "très lentement, car elle eut tout le loisir, en descendant, de regarder autour d'elle et "
    "de se demander ce qui allait bien pouvoir arriver ensuite. D'abord, elle essaya de "
    "regarder en bas pour voir où elle arrivait, mais il faisait trop sombre pour rien "
    "distinguer ; puis elle observa les parois du puits, et remarqua qu'elles étaient garnies "
    "de placards et d'étagères à livres ; ici et là, elle vit des cartes et des images "
    "accrochées à des patères. Elle prit au passage un pot sur une des étagères ; il portait "
    "l'étiquette MARMELADE D'ORANGES, mais, à sa grande déception, il était vide. Elle n'eut "
    "pas le cœur de laisser tomber le pot, de peur de tuer quelqu'un en dessous, et parvint à "
    "le glisser dans un des placards en tombant devant. « Eh bien ! se dit Alice, après une "
    "chute pareille, tomber dans l'escalier ne me fera plus rien du tout ! Comme on va me "
    "trouver courageuse, à la maison ! Tiens, je ne dirais même pas un mot si je tombais du "
    "haut de la maison ! » (Ce qui était bien probable.) Elle tombait, tombait, tombait. La "
    "chute n'allait-elle donc jamais finir ? « Je me demande combien de milles j'ai bien pu "
    "tomber, à l'heure qu'il est ? dit-elle tout haut. Je dois être en train d'approcher du "
    "centre de la Terre. Voyons voir : cela ferait dans les quatre mille milles, je crois. » "
    "(Car, voyez-vous, Alice avait appris plusieurs choses de ce genre dans ses leçons, en "
    "classe, et bien que ce ne fût pas une très bonne occasion de faire étalage de son "
    "savoir, puisque personne n'était là pour l'écouter, c'était tout de même un bon "
    "exercice que de se le répéter.) « Oui, ça doit être à peu près la bonne distance. Mais "
    "alors, je me demande à quelle latitude et à quelle longitude je suis rendue ? »"
)

_TOM_SAWYER_SOURCE = (
    "To my wife this book is affectionately dedicated. Most of the adventures recorded in "
    "this book really occurred; one or two were experiences of my own, the rest those of "
    "boys who were schoolmates of mine. Huck Finn is drawn from life; Tom Sawyer also, but "
    "not from an individual — he is a combination of the characteristics of three boys whom "
    "I knew, and therefore belongs to the composite order of architecture. The odd "
    "superstitions touched upon were all prevalent among children and slaves in the West at "
    "the period of this story. Although my book is intended mainly for the entertainment of "
    "boys and girls, I hope it will not be shunned by men and women on that account, for "
    "part of my plan has been to try to pleasantly remind adults of what they once were "
    "themselves, and of how they felt and thought and talked, and what queer enterprises "
    "they sometimes engaged in."
)

_TOM_SAWYER_FR = (
    "À ma femme, ce livre est affectueusement dédié. La plupart des aventures racontées dans "
    "ce livre se sont réellement produites ; une ou deux sont tirées de ma propre expérience, "
    "le reste de celle de garçons qui furent mes camarades d'école. Huck Finn est pris sur le "
    "vif ; Tom Sawyer aussi, mais pas d'après un individu unique — il réunit les "
    "caractéristiques de trois garçons que j'ai connus, et appartient donc à l'ordre "
    "architectural composite. Les curieuses superstitions évoquées ici étaient toutes "
    "répandues parmi les enfants et les esclaves de l'Ouest à l'époque de cette histoire. "
    "Bien que ce livre soit destiné avant tout à l'amusement des garçons et des filles, "
    "j'espère que les hommes et les femmes ne le bouderont pas pour autant, car une partie "
    "de mon dessein a été d'essayer de rappeler agréablement aux adultes ce qu'ils furent "
    "eux-mêmes autrefois, ce qu'ils ressentaient, pensaient et disaient, et dans quelles "
    "curieuses entreprises ils se lançaient parfois."
)

_MOBY_DICK_SOURCE = (
    "Call me Ishmael. Some years ago — never mind how long precisely — having little or no "
    "money in my purse, and nothing particular to interest me on shore, I thought I would "
    "sail about a little and see the watery part of the world. It is a way I have of driving "
    "off the spleen and regulating the circulation. Whenever I find myself growing grim "
    "about the mouth; whenever it is a damp, drizzly November in my soul; whenever I find "
    "myself involuntarily pausing before coffin warehouses, and bringing up the rear of "
    "every funeral I meet; and especially whenever my hypos get such an upper hand of me, "
    "that it requires a strong moral principle to prevent me from deliberately stepping into "
    "the street, and methodically knocking people's hats off — then, I account it high time "
    "to get to sea as soon as I can."
)

_MOBY_DICK_FR = (
    "Appelez-moi Ismaël. Il y a quelques années — peu importe combien exactement — n'ayant "
    "que peu ou pas d'argent dans ma bourse, et rien de particulier pour m'intéresser à "
    "terre, je pensai que j'irais naviguer un peu et voir la partie aquatique du monde. "
    "C'est une façon que j'ai de chasser la mélancolie et de régler ma circulation. Chaque "
    "fois que je me sens devenir sombre autour de la bouche ; chaque fois que c'est un "
    "humide et pluvieux mois de novembre dans mon âme ; chaque fois que je me surprends à "
    "m'arrêter involontairement devant des entrepôts de cercueils, et à fermer la marche de "
    "tous les enterrements que je croise ; et surtout, chaque fois que mes idées noires "
    "prennent sur moi un tel ascendant qu'il me faut un solide principe moral pour "
    "m'empêcher de descendre délibérément dans la rue et de faire tomber méthodiquement le "
    "chapeau des passants — alors, j'estime qu'il est grand temps pour moi de prendre la mer "
    "dès que possible."
)

_LUXUN_SOURCE = (
    "星期日的早晨，我揭去一张隔夜的日历，向着新的那一张上看了又看的说：「阿，十月十日，——"
    "今天原来正是双十节。这里却一点没有记载！」我的一位前辈先生N，正走到我的寓里来谈闲天，"
    "一听这话，便很不高兴的对我说：「他们对！他们不记得，你怎样他；你记得，又怎样呢？」"
)

_LUXUN_FR = (
    "Un dimanche matin, j'arrachai la page de la veille sur le calendrier, et en regardant "
    "la nouvelle page, je répétai à plusieurs reprises : « Ah, le dix octobre — c'est "
    "justement aujourd'hui la fête du Double Dix ! Et il n'y a pourtant rien d'inscrit ici ! » "
    "Mon aîné, Monsieur N, venait justement chez moi pour bavarder ; en entendant cela, il "
    "me dit, plutôt mécontent : « Ils ont raison ! Ils ne s'en souviennent pas — qu'est-ce "
    "que tu peux y faire ? Toi, tu t'en souviens — et alors, qu'est-ce que ça change ? »"
)

_GMU_SOURCE = (
    "Please call Stella. Ask her to bring these things with her from the store: Six spoons "
    "of fresh snow peas, five thick slabs of blue cheese, and maybe a snack for her brother "
    "Bob. We also need a small plastic snake and a big toy frog for the kids. She can scoop "
    "these things into three red bags, and we will go meet her Wednesday at the train "
    "station."
)

_GMU_FR = (
    "Appelle Stella, s'il te plaît. Demande-lui de rapporter ces choses du magasin en "
    "venant : six cuillères de pois mange-tout frais, cinq tranches épaisses de fromage "
    "bleu, et peut-être un en-cas pour son frère Bob. Il nous faut aussi un petit serpent en "
    "plastique et une grosse grenouille en peluche pour les enfants. Elle peut mettre tout "
    "ça dans trois sacs rouges, et nous irons la retrouver mercredi à la gare."
)


REFERENCE_TRANSCRIPTS: dict[str, ReferenceTranscript] = {
    "a": ReferenceTranscript(
        key="a",
        source_text=_ALICE_SOURCE,
        fr_reference=_ALICE_FR,
        provenance="Alice's Adventures in Wonderland, Lewis Carroll, ch.1 (texte original, "
        "domaine public) — ne couvre pas l'annonce LibriVox en tête de fichier ni la toute "
        "fin de l'audio (~185s, cf. corpus.py), juste le corps du texte.",
    ),
    "b": ReferenceTranscript(
        key="b",
        source_text=f"{_TOM_SAWYER_SOURCE}\n{_MOBY_DICK_SOURCE}",
        fr_reference=f"{_TOM_SAWYER_FR}\n{_MOBY_DICK_FR}",
        provenance="The Adventures of Tom Sawyer, Mark Twain (dédicace+préface) + Moby-Dick, "
        "Herman Melville (ouverture ch.1) — textes originaux, domaine public. ⚠ Les deux "
        "textes se chevauchent réellement dans l'audio (20-65s, cf. corpus.py) : la "
        "concaténation ci-dessus ne reflète pas l'entrelacement temporel réel, le diff peut "
        "montrer du désordre à cause de ça, pas seulement des erreurs de pipeline.",
    ),
    "c": ReferenceTranscript(
        key="c",
        source_text=_LUXUN_SOURCE,
        fr_reference=_LUXUN_FR,
        provenance="头发的故事 (« Histoire de cheveux »), Lu Xun, recueil 呐喊, premières "
        "lignes (texte original, domaine public, vérifié via recherche web le 2026-07-15) — "
        "⚠ ne couvre que le tout début du texte, l'audio (~60s) peut aller au-delà ; annonce "
        "LibriVox en tête probable, non incluse ici (cf. note en tête de fichier).",
    ),
    "d": ReferenceTranscript(
        key="d",
        source_text=_GMU_SOURCE,
        fr_reference=_GMU_FR,
        provenance="Paragraphe standard du Speech Accent Archive (accent.gmu.edu/howto.php), "
        "vérifié par lecture directe le 2026-07-15. ⚠ Le fichier corpus boucle ce même "
        "paragraphe pour atteindre min_duration_s (cf. corpus.py) — la référence ci-dessus ne "
        "couvre qu'une occurrence, le diff montrera probablement une répétition dans la "
        "sortie pipeline si le bouclage est audible dans le clip.",
    ),
}

_ALIASES = {"e": "b", "f": "b"}


def get_reference(corpus_key: str) -> ReferenceTranscript:
    """Retourne la référence pour `corpus_key` — `e`/`f` pointent vers `b` (mêmes
    locuteurs/texte, cf. `corpus.py` : seul le bruit de fond ajouté diffère)."""
    return REFERENCE_TRANSCRIPTS[_ALIASES.get(corpus_key, corpus_key)]
