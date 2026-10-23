const fs = require('fs');
const path = require('path');

// 2024 Historical Rulers, Heads of Government and Key Cabinet Ministers
// Ages as of 1 January 2024 based on real-world birth dates.

const data = {
  // --- North America & Caribbean ---
  "USA": {
    "head_of_state": "Joe Biden",
    "state_age": 81,
    "state_trait": "diplomat",
    "ministers": {
      "foreign": { "name": "Antony Blinken", "age": 61, "trait": "diplomat", "option": "coalition_builder" },
      "defense": { "name": "Lloyd Austin", "age": 70, "trait": "veteran_commander", "option": "quartermaster_general" },
      "economy": { "name": "Janet Yellen", "age": 77, "trait": "technocrat", "option": "orthodox_treasurer" },
      "interior": { "name": "Merrick Garland", "age": 71, "trait": "reformer", "option": "constitutional_jurist" }
    }
  },
  "CAN": {
    "head_of_state": "Charles III",
    "head_of_government": "Justin Trudeau",
    "state_age": 75,
    "gov_age": 52,
    "state_trait": "diplomat",
    "gov_trait": "reformer",
    "ministers": {
      "foreign": { "name": "Melanie Joly", "age": 44, "trait": "diplomat", "option": "coalition_builder" },
      "defense": { "name": "Bill Blair", "age": 69, "trait": "reformer", "option": "quartermaster_general" },
      "economy": { "name": "Chrystia Freeland", "age": 55, "trait": "technocrat", "option": "orthodox_treasurer" },
      "interior": { "name": "Dominic LeBlanc", "age": 56, "trait": "reformer", "option": "constitutional_jurist" }
    }
  },
  "MEX": {
    "head_of_state": "Andres Manuel Lopez Obrador",
    "state_age": 70,
    "state_trait": "populist",
    "ministers": {
      "foreign": { "name": "Alicia Barcena", "age": 71, "trait": "diplomat", "option": "coalition_builder" },
      "defense": { "name": "Luis Cresencio Sandoval", "age": 63, "trait": "veteran_commander", "option": "deep_battle_marshal" },
      "economy": { "name": "Rogelio Ramirez de la O", "age": 75, "trait": "technocrat", "option": "orthodox_treasurer" },
      "interior": { "name": "Luisa Maria Alcalde", "age": 36, "trait": "reformer", "option": "constitutional_jurist" }
    }
  },
  "CUB": { "head_of_state": "Miguel Diaz-Canel", "head_of_government": "Manuel Marrero", "state_age": 63, "gov_age": 60, "state_trait": "ideologue", "gov_trait": "welfare_architect" },
  "HTI": { "head_of_state": "Ariel Henry", "state_age": 74, "state_trait": "technocrat" },
  "DOM": { "head_of_state": "Luis Abinader", "state_age": 56, "state_trait": "reformer" },
  "JAM": { "head_of_state": "Charles III", "head_of_government": "Andrew Holness", "state_age": 75, "gov_age": 51, "state_trait": "diplomat", "gov_trait": "technocrat" },
  "TTO": { "head_of_state": "Christine Kangaloo", "head_of_government": "Keith Rowley", "state_age": 62, "gov_age": 74, "state_trait": "reformer", "gov_trait": "populist" },
  "BHS": { "head_of_state": "Charles III", "head_of_government": "Philip Davis", "state_age": 75, "gov_age": 72, "state_trait": "diplomat", "gov_trait": "reformer" },
  "BRB": { "head_of_state": "Sandra Mason", "head_of_government": "Mia Mottley", "state_age": 74, "gov_age": 58, "state_trait": "reformer", "gov_trait": "populist" },
  "BLZ": { "head_of_state": "Charles III", "head_of_government": "Johnny Briceno", "state_age": 75, "gov_age": 63, "state_trait": "diplomat", "gov_trait": "reformer" },
  "GTM": { "head_of_state": "Alejandro Giammattei", "state_age": 67, "state_trait": "iron_hand" },
  "HND": { "head_of_state": "Xiomara Castro", "state_age": 64, "state_trait": "populist" },
  "SLV": { "head_of_state": "Nayib Bukele", "state_age": 42, "state_trait": "populist" },
  "NIC": { "head_of_state": "Daniel Ortega", "state_age": 78, "state_trait": "iron_hand" },
  "CRI": { "head_of_state": "Rodrigo Chaves Robles", "state_age": 62, "state_trait": "technocrat" },
  "PAN": { "head_of_state": "Laurentino Cortizo", "state_age": 70, "state_trait": "reformer" },
  "PRI": { "head_of_state": "Pedro Pierluisi", "state_age": 64, "state_trait": "technocrat" },

  // --- South America ---
  "BRA": {
    "head_of_state": "Luiz Inacio Lula da Silva",
    "state_age": 78,
    "state_trait": "welfare_architect",
    "ministers": {
      "foreign": { "name": "Mauro Vieira", "age": 72, "trait": "diplomat", "option": "coalition_builder" },
      "defense": { "name": "Jose Mucio", "age": 75, "trait": "diplomat", "option": "quartermaster_general" },
      "economy": { "name": "Fernando Haddad", "age": 61, "trait": "technocrat", "option": "orthodox_treasurer" },
      "interior": { "name": "Ricardo Lewandowski", "age": 75, "trait": "reformer", "option": "constitutional_jurist" }
    }
  },
  "ARG": {
    "head_of_state": "Javier Milei",
    "state_age": 53,
    "state_trait": "populist",
    "ministers": {
      "foreign": { "name": "Diana Mondino", "age": 65, "trait": "technocrat", "option": "trade_negotiator" },
      "defense": { "name": "Luis Petri", "age": 46, "trait": "hawk", "option": "offensive_strategist" },
      "economy": { "name": "Luis Caputo", "age": 58, "trait": "technocrat", "option": "orthodox_treasurer" },
      "interior": { "name": "Guillermo Francos", "age": 73, "trait": "diplomat", "option": "constitutional_jurist" }
    }
  },
  "COL": { "head_of_state": "Gustavo Petro", "state_age": 63, "state_trait": "welfare_architect" },
  "VEN": { "head_of_state": "Nicolas Maduro", "state_age": 61, "state_trait": "populist" },
  "CHL": { "head_of_state": "Gabriel Boric", "state_age": 37, "state_trait": "welfare_architect" },
  "PER": { "head_of_state": "Dina Boluarte", "state_age": 61, "state_trait": "reformer" },
  "ECU": { "head_of_state": "Daniel Noboa", "state_age": 36, "state_trait": "technocrat" },
  "BOL": { "head_of_state": "Luis Arce", "state_age": 60, "state_trait": "technocrat" },
  "PRY": { "head_of_state": "Santiago Pena", "state_age": 45, "state_trait": "technocrat" },
  "URY": { "head_of_state": "Luis Lacalle Pou", "state_age": 50, "state_trait": "reformer" },
  "GUY": { "head_of_state": "Irfaan Ali", "head_of_government": "Mark Phillips", "state_age": 43, "gov_age": 62, "state_trait": "industrialist", "gov_trait": "veteran_commander" },
  "SUR": { "head_of_state": "Chan Santokhi", "state_age": 64, "state_trait": "reformer" },

  // --- Western & Northern Europe ---
  "GBR": {
    "head_of_state": "Charles III",
    "head_of_government": "Rishi Sunak",
    "state_age": 75,
    "gov_age": 43,
    "state_trait": "diplomat",
    "gov_trait": "technocrat",
    "ministers": {
      "foreign": { "name": "David Cameron", "age": 57, "trait": "diplomat", "option": "coalition_builder" },
      "defense": { "name": "Grant Shapps", "age": 55, "trait": "hawk", "option": "offensive_strategist" },
      "economy": { "name": "Jeremy Hunt", "age": 57, "trait": "technocrat", "option": "orthodox_treasurer" },
      "interior": { "name": "James Cleverly", "age": 54, "trait": "iron_hand", "option": "security_chief" }
    }
  },
  "FRA": {
    "head_of_state": "Emmanuel Macron",
    "head_of_government": "Gabriel Attal",
    "state_age": 46,
    "gov_age": 34,
    "state_trait": "reformer",
    "gov_trait": "reformer",
    "ministers": {
      "foreign": { "name": "Stephane Sejourne", "age": 38, "trait": "diplomat", "option": "coalition_builder" },
      "defense": { "name": "Sebastien Lecornu", "age": 37, "trait": "hawk", "option": "quartermaster_general" },
      "economy": { "name": "Bruno Le Maire", "age": 54, "trait": "technocrat", "option": "orthodox_treasurer" },
      "interior": { "name": "Gerald Darmanin", "age": 41, "trait": "iron_hand", "option": "security_chief" }
    }
  },
  "DEU": {
    "head_of_state": "Frank-Walter Steinmeier",
    "head_of_government": "Olaf Scholz",
    "state_age": 67,
    "gov_age": 65,
    "state_trait": "diplomat",
    "gov_trait": "welfare_architect",
    "ministers": {
      "foreign": { "name": "Annalena Baerbock", "age": 43, "trait": "diplomat", "option": "coalition_builder" },
      "defense": { "name": "Boris Pistorius", "age": 63, "trait": "veteran_commander", "option": "quartermaster_general" },
      "economy": { "name": "Christian Lindner", "age": 44, "trait": "technocrat", "option": "orthodox_treasurer" },
      "interior": { "name": "Nancy Faeser", "age": 53, "trait": "reformer", "option": "constitutional_jurist" }
    }
  },
  "ITA": {
    "head_of_state": "Sergio Mattarella",
    "head_of_government": "Giorgia Meloni",
    "state_age": 82,
    "gov_age": 46,
    "state_trait": "reformer",
    "gov_trait": "populist",
    "ministers": {
      "foreign": { "name": "Antonio Tajani", "age": 70, "trait": "diplomat", "option": "coalition_builder" },
      "defense": { "name": "Guido Crosetto", "age": 60, "trait": "hawk", "option": "quartermaster_general" },
      "economy": { "name": "Giancarlo Giorgetti", "age": 57, "trait": "technocrat", "option": "orthodox_treasurer" },
      "interior": { "name": "Matteo Piantedosi", "age": 60, "trait": "iron_hand", "option": "security_chief" }
    }
  },
  "ESP": {
    "head_of_state": "Felipe VI",
    "head_of_government": "Pedro Sanchez",
    "state_age": 55,
    "gov_age": 51,
    "state_trait": "diplomat",
    "gov_trait": "welfare_architect",
    "ministers": {
      "foreign": { "name": "Jose Manuel Albares", "age": 51, "trait": "diplomat", "option": "coalition_builder" },
      "defense": { "name": "Margarita Robles", "age": 67, "trait": "reformer", "option": "quartermaster_general" },
      "economy": { "name": "Carlos Cuerpo", "age": 43, "trait": "technocrat", "option": "orthodox_treasurer" },
      "interior": { "name": "Fernando Grande-Marlaska", "age": 61, "trait": "iron_hand", "option": "security_chief" }
    }
  },
  "PRT": { "head_of_state": "Marcelo Rebelo de Sousa", "head_of_government": "Antonio Costa", "state_age": 75, "gov_age": 62, "state_trait": "diplomat", "gov_trait": "welfare_architect" },
  "NLD": { "head_of_state": "Willem-Alexander", "head_of_government": "Mark Rutte", "state_age": 56, "gov_age": 56, "state_trait": "diplomat", "gov_trait": "reformer" },
  "BEL": { "head_of_state": "Philippe", "head_of_government": "Alexander De Croo", "state_age": 63, "gov_age": 48, "state_trait": "diplomat", "gov_trait": "technocrat" },
  "LUX": { "head_of_state": "Henri", "head_of_government": "Luc Frieden", "state_age": 68, "gov_age": 60, "state_trait": "diplomat", "gov_trait": "technocrat" },
  "IRL": { "head_of_state": "Michael D. Higgins", "head_of_government": "Leo Varadkar", "state_age": 82, "gov_age": 44, "state_trait": "diplomat", "gov_trait": "reformer" },
  "CHE": { "head_of_state": "Viola Amherd", "state_age": 61, "state_trait": "reformer" },
  "AUT": { "head_of_state": "Alexander Van der Bellen", "head_of_government": "Karl Nehammer", "state_age": 79, "gov_age": 51, "state_trait": "diplomat", "gov_trait": "reformer" },
  "SWE": { "head_of_state": "Carl XVI Gustaf", "head_of_government": "Ulf Kristersson", "state_age": 77, "gov_age": 60, "state_trait": "diplomat", "gov_trait": "reformer" },
  "NOR": { "head_of_state": "Harald V", "head_of_government": "Jonas Gahr Store", "state_age": 86, "gov_age": 63, "state_trait": "diplomat", "gov_trait": "welfare_architect" },
  "DNK": { "head_of_state": "Margrethe II", "head_of_government": "Mette Frederiksen", "state_age": 83, "gov_age": 46, "state_trait": "diplomat", "gov_trait": "welfare_architect" },
  "FIN": { "head_of_state": "Sauli Niinisto", "head_of_government": "Petteri Orpo", "state_age": 75, "gov_age": 54, "state_trait": "diplomat", "gov_trait": "technocrat" },
  "ISL": { "head_of_state": "Gudni Th. Johannesson", "head_of_government": "Katrin Jakobsdottir", "state_age": 55, "gov_age": 47, "state_trait": "diplomat", "gov_trait": "reformer" },

  // Microstates
  "MCO": { "head_of_state": "Albert II", "head_of_government": "Pierre Dartout", "state_age": 65, "gov_age": 69, "state_trait": "diplomat", "gov_trait": "technocrat" },
  "AND": { "head_of_state": "Joan Enric Vives i Sicilia", "head_of_government": "Xavier Espot Zamora", "state_age": 74, "gov_age": 44, "state_trait": "diplomat", "gov_trait": "reformer" },
  "LIE": { "head_of_state": "Hans-Adam II", "head_of_government": "Daniel Risch", "state_age": 78, "gov_age": 45, "state_trait": "diplomat", "gov_trait": "technocrat" },
  "SMR": { "head_of_state": "Filippo Tamagnini", "state_age": 53, "state_trait": "reformer" },
  "VAT": { "head_of_state": "Pope Francis", "head_of_government": "Pietro Parolin", "state_age": 87, "gov_age": 68, "state_trait": "diplomat", "gov_trait": "technocrat" },

  // --- Central & Eastern Europe ---
  "RUS": {
    "head_of_state": "Vladimir Putin",
    "head_of_government": "Mikhail Mishustin",
    "state_age": 71,
    "gov_age": 57,
    "state_trait": "hawk",
    "gov_trait": "technocrat",
    "ministers": {
      "foreign": { "name": "Sergei Lavrov", "age": 73, "trait": "diplomat", "option": "realpolitik_envoy" },
      "defense": { "name": "Sergei Shoigu", "age": 68, "trait": "veteran_commander", "option": "quartermaster_general" },
      "economy": { "name": "Anton Siluanov", "age": 60, "trait": "technocrat", "option": "orthodox_treasurer" },
      "interior": { "name": "Vladimir Kolokoltsev", "age": 62, "trait": "iron_hand", "option": "security_chief" }
    }
  },
  "UKR": {
    "head_of_state": "Volodymyr Zelenskyy",
    "head_of_government": "Denys Shmyhal",
    "state_age": 45,
    "gov_age": 48,
    "state_trait": "hawk",
    "gov_trait": "industrialist",
    "ministers": {
      "foreign": { "name": "Dmytro Kuleba", "age": 42, "trait": "diplomat", "option": "coalition_builder" },
      "defense": { "name": "Rustem Umerov", "age": 41, "trait": "reformer", "option": "quartermaster_general" },
      "economy": { "name": "Sergii Marchenko", "age": 42, "trait": "technocrat", "option": "orthodox_treasurer" },
      "interior": { "name": "Ihor Klymenko", "age": 51, "trait": "iron_hand", "option": "security_chief" }
    }
  },
  "BLR": {
    "head_of_state": "Alexander Lukashenko",
    "head_of_government": "Roman Golovchenko",
    "state_age": 69,
    "gov_age": 50,
    "state_trait": "iron_hand",
    "gov_trait": "industrialist",
    "ministers": {
      "foreign": { "name": "Sergei Aleinik", "age": 58, "trait": "diplomat", "option": "coalition_builder" },
      "defense": { "name": "Viktor Khrenin", "age": 52, "trait": "veteran_commander", "option": "deep_battle_marshal" },
      "economy": { "name": "Yuri Chebotar", "age": 47, "trait": "industrialist", "option": "industrial_mobiliser" },
      "interior": { "name": "Ivan Kubrakov", "age": 48, "trait": "iron_hand", "option": "security_chief" }
    }
  },
  "POL": {
    "head_of_state": "Andrzej Duda",
    "head_of_government": "Donald Tusk",
    "state_age": 51,
    "gov_age": 66,
    "state_trait": "hawk",
    "gov_trait": "reformer",
    "ministers": {
      "foreign": { "name": "Radoslaw Sikorski", "age": 60, "trait": "diplomat", "option": "coalition_builder" },
      "defense": { "name": "Wladyslaw Kosiniak-Kamysz", "age": 42, "trait": "reformer", "option": "quartermaster_general" },
      "economy": { "name": "Andrzej Domanski", "age": 42, "trait": "technocrat", "option": "orthodox_treasurer" },
      "interior": { "name": "Marcin Kierwinski", "age": 47, "trait": "reformer", "option": "constitutional_jurist" }
    }
  },
  "CZE": { "head_of_state": "Petr Pavel", "head_of_government": "Petr Fiala", "state_age": 62, "gov_age": 59, "state_trait": "veteran_commander", "gov_trait": "reformer" },
  "SVK": { "head_of_state": "Zuzana Caputova", "head_of_government": "Robert Fico", "state_age": 50, "gov_age": 59, "state_trait": "reformer", "gov_trait": "populist" },
  "HUN": { "head_of_state": "Katalin Novak", "head_of_government": "Viktor Orban", "state_age": 46, "gov_age": 60, "state_trait": "diplomat", "gov_trait": "populist" },
  "ROU": { "head_of_state": "Klaus Iohannis", "head_of_government": "Marcel Ciolacu", "state_age": 64, "gov_age": 56, "state_trait": "diplomat", "gov_trait": "welfare_architect" },
  "BGR": { "head_of_state": "Rumen Radev", "head_of_government": "Nikolai Denkov", "state_age": 60, "gov_age": 61, "state_trait": "veteran_commander", "gov_trait": "technocrat" },
  "MDA": { "head_of_state": "Maia Sandu", "head_of_government": "Dorin Recean", "state_age": 51, "gov_age": 49, "state_trait": "reformer", "gov_trait": "technocrat" },
  "EST": { "head_of_state": "Alar Karis", "head_of_government": "Kaja Kallas", "state_age": 65, "gov_age": 46, "state_trait": "diplomat", "gov_trait": "hawk" },
  "LVA": { "head_of_state": "Edgars Rinkevics", "head_of_government": "Evika Silina", "state_age": 50, "gov_age": 48, "state_trait": "diplomat", "gov_trait": "reformer" },
  "LTU": { "head_of_state": "Gitanas Nauseda", "head_of_government": "Ingrida Simonyte", "state_age": 59, "gov_age": 49, "state_trait": "hawk", "gov_trait": "technocrat" },

  // Balkans
  "SRB": { "head_of_state": "Aleksandar Vucic", "head_of_government": "Ana Brnabic", "state_age": 53, "gov_age": 48, "state_trait": "populist", "gov_trait": "technocrat" },
  "HRV": { "head_of_state": "Zoran Milanovic", "head_of_government": "Andrej Plenkovic", "state_age": 57, "gov_age": 53, "state_trait": "populist", "gov_trait": "technocrat" },
  "SVN": { "head_of_state": "Natasa Pirc Musar", "head_of_government": "Robert Golob", "state_age": 55, "gov_age": 56, "state_trait": "reformer", "gov_trait": "technocrat" },
  "BIH": { "head_of_state": "Zeljko Komsic", "head_of_government": "Borjana Kristo", "state_age": 59, "gov_age": 62, "state_trait": "diplomat", "gov_trait": "technocrat" },
  "MNE": { "head_of_state": "Jakov Milatovic", "head_of_government": "Milojko Spajic", "state_age": 37, "gov_age": 36, "state_trait": "technocrat", "gov_trait": "technocrat" },
  "MKD": { "head_of_state": "Stevo Pendarovski", "head_of_government": "Dimitar Kovachevski", "state_age": 60, "gov_age": 49, "state_trait": "diplomat", "gov_trait": "technocrat" },
  "ALB": { "head_of_state": "Bajram Begaj", "head_of_government": "Edi Rama", "state_age": 56, "gov_age": 59, "state_trait": "veteran_commander", "gov_trait": "reformer" },
  "GRC": { "head_of_state": "Katerina Sakellaropoulou", "head_of_government": "Kyriakos Mitsotakis", "state_age": 67, "gov_age": 55, "state_trait": "reformer", "gov_trait": "technocrat" },
  "CYP": { "head_of_state": "Nikos Christodoulides", "state_age": 50, "state_trait": "diplomat" },
  "MLT": { "head_of_state": "George Vella", "head_of_government": "Robert Abela", "state_age": 81, "gov_age": 46, "state_trait": "diplomat", "gov_trait": "reformer" },
  "KOS": { "head_of_state": "Vjosa Osmani", "head_of_government": "Albin Kurti", "state_age": 41, "gov_age": 48, "state_trait": "reformer", "gov_trait": "hawk" },
  "PMR": { "head_of_state": "Vadim Krasnoselsky", "head_of_government": "Aleksandr Rozenberg", "state_age": 53, "gov_age": 56, "state_trait": "iron_hand", "gov_trait": "industrialist" },

  // --- Middle East & North Africa ---
  "TUR": {
    "head_of_state": "Recep Tayyip Erdogan",
    "state_age": 69,
    "state_trait": "hawk",
    "ministers": {
      "foreign": { "name": "Hakan Fidan", "age": 56, "trait": "iron_hand", "option": "realpolitik_envoy" },
      "defense": { "name": "Yasar Guler", "age": 70, "trait": "veteran_commander", "option": "deep_battle_marshal" },
      "economy": { "name": "Mehmet Simsek", "age": 57, "trait": "technocrat", "option": "orthodox_treasurer" },
      "interior": { "name": "Ali Yerlikaya", "age": 55, "trait": "reformer", "option": "security_chief" }
    }
  },
  "ISR": {
    "head_of_state": "Isaac Herzog",
    "head_of_government": "Benjamin Netanyahu",
    "state_age": 63,
    "gov_age": 74,
    "state_trait": "diplomat",
    "gov_trait": "hawk",
    "ministers": {
      "foreign": { "name": "Israel Katz", "age": 68, "trait": "hawk", "option": "realpolitik_envoy" },
      "defense": { "name": "Yoav Gallant", "age": 69, "trait": "veteran_commander", "option": "offensive_strategist" },
      "economy": { "name": "Bezalel Smotrich", "age": 43, "trait": "ideologue", "option": "orthodox_treasurer" },
      "interior": { "name": "Itamar Ben-Gvir", "age": 47, "trait": "populist", "option": "security_chief" }
    }
  },
  "SAU": { "head_of_state": "Salman bin Abdulaziz", "head_of_government": "Mohammed bin Salman", "state_age": 88, "gov_age": 38, "state_trait": "iron_hand", "gov_trait": "industrialist" },
  "IRN": { "head_of_state": "Ali Khamenei", "head_of_government": "Ebrahim Raisi", "state_age": 84, "gov_age": 63, "state_trait": "ideologue", "gov_trait": "iron_hand" },
  "IRQ": { "head_of_state": "Abdul Latif Rashid", "head_of_government": "Mohammed Shia al-Sudani", "state_age": 79, "gov_age": 53, "state_trait": "diplomat", "gov_trait": "reformer" },
  "SYR": { "head_of_state": "Bashar al-Assad", "state_age": 58, "state_trait": "iron_hand" },
  "LBN": { "head_of_state": "Najib Mikati", "head_of_government": "Najib Mikati", "state_age": 68, "gov_age": 68, "state_trait": "technocrat", "gov_trait": "technocrat" },
  "JOR": { "head_of_state": "Abdullah II", "head_of_government": "Bisher Khasawneh", "state_age": 61, "gov_age": 54, "state_trait": "diplomat", "gov_trait": "reformer" },
  "ARE": { "head_of_state": "Mohamed bin Zayed Al Nahyan", "head_of_government": "Mohammed bin Rashid Al Maktoum", "state_age": 62, "gov_age": 74, "state_trait": "hawk", "gov_trait": "industrialist" },
  "QAT": { "head_of_state": "Tamim bin Hamad Al Thani", "head_of_government": "Mohammed bin Abdulrahman Al Thani", "state_age": 43, "gov_age": 43, "state_trait": "diplomat", "gov_trait": "diplomat" },
  "KWT": { "head_of_state": "Mishal Al-Ahmad Al-Jaber Al-Sabah", "head_of_government": "Mohammad Sabah Al-Salem Al-Sabah", "state_age": 83, "gov_age": 68, "state_trait": "iron_hand", "gov_trait": "technocrat" },
  "BHR": { "head_of_state": "Hamad bin Isa Al Khalifa", "head_of_government": "Salman bin Hamad Al Khalifa", "state_age": 73, "gov_age": 54, "state_trait": "iron_hand", "gov_trait": "reformer" },
  "OMN": { "head_of_state": "Haitham bin Tariq", "state_age": 69, "state_trait": "diplomat" },
  "YEM": { "head_of_state": "Rashad al-Alimi", "head_of_government": "Maeen Abdulmalik Saeed", "state_age": 69, "gov_age": 47, "state_trait": "diplomat", "gov_trait": "reformer" },
  "HTH": { "head_of_state": "Abdul-Malik al-Houthi", "head_of_government": "Mahdi al-Mashat", "state_age": 44, "gov_age": 38, "state_trait": "ideologue", "gov_trait": "veteran_commander" },
  "STC": { "head_of_state": "Aidarus al-Zoubaidi", "state_age": 56, "state_trait": "veteran_commander" },
  "PSE": { "head_of_state": "Mahmoud Abbas", "head_of_government": "Mohammad Shtayyeh", "state_age": 88, "gov_age": 65, "state_trait": "diplomat", "gov_trait": "technocrat" },
  "EGY": { "head_of_state": "Abdel Fattah el-Sisi", "head_of_government": "Mostafa Madbouly", "state_age": 69, "gov_age": 57, "state_trait": "veteran_commander", "gov_trait": "industrialist" },
  "LBY": { "head_of_state": "Mohamed al-Menfi", "head_of_government": "Abdul Hamid Dbeibeh", "state_age": 47, "gov_age": 64, "state_trait": "diplomat", "gov_trait": "technocrat" },
  "LNA": { "head_of_state": "Khalifa Haftar", "state_age": 80, "state_trait": "veteran_commander" },
  "TUN": { "head_of_state": "Kais Saied", "head_of_government": "Ahmed Hachani", "state_age": 65, "gov_age": 67, "state_trait": "populist", "gov_trait": "technocrat" },
  "DZA": { "head_of_state": "Abdelmadjid Tebboune", "head_of_government": "Nadir Larbaoui", "state_age": 78, "gov_age": 74, "state_trait": "veteran_commander", "gov_trait": "diplomat" },
  "MAR": { "head_of_state": "Mohammed VI", "head_of_government": "Aziz Akhannouch", "state_age": 60, "gov_age": 62, "state_trait": "diplomat", "gov_trait": "industrialist" },
  "MRT": { "head_of_state": "Mohamed Ould Ghazouani", "head_of_government": "Mohamed Ould Bilal", "state_age": 67, "gov_age": 61, "state_trait": "veteran_commander", "gov_trait": "technocrat" },
  "SDN": { "head_of_state": "Abdel Fattah al-Burhan", "state_age": 63, "state_trait": "veteran_commander" },
  "RSF": { "head_of_state": "Mohamed Hamdan Dagalo", "state_age": 49, "state_trait": "hawk" },
  "SSD": { "head_of_state": "Salva Kiir Mayardit", "state_age": 72, "state_trait": "veteran_commander" },

  // --- Caucasus & Central Asia ---
  "GEO": { "head_of_state": "Salome Zourabichvili", "head_of_government": "Irakli Garibashvili", "state_age": 71, "gov_age": 41, "state_trait": "diplomat", "gov_trait": "populist" },
  "ARM": { "head_of_state": "Vahagn Khachaturyan", "head_of_government": "Nikol Pashinyan", "state_age": 64, "gov_age": 48, "state_trait": "technocrat", "gov_trait": "reformer" },
  "AZE": { "head_of_state": "Ilham Aliyev", "head_of_government": "Ali Asadov", "state_age": 62, "gov_age": 67, "state_trait": "hawk", "gov_trait": "technocrat" },
  "ABK": { "head_of_state": "Aslan Bzhania", "head_of_government": "Aleksandr Ankvab", "state_age": 60, "gov_age": 71, "state_trait": "iron_hand", "gov_trait": "veteran_commander" },
  "OST": { "head_of_state": "Alan Gagloev", "head_of_government": "Konstantin Dzhussoev", "state_age": 42, "gov_age": 54, "state_trait": "iron_hand", "gov_trait": "industrialist" },
  "KAZ": { "head_of_state": "Kassym-Jomart Tokayev", "head_of_government": "Alikhan Smailov", "state_age": 70, "gov_age": 51, "state_trait": "diplomat", "gov_trait": "technocrat" },
  "UZB": { "head_of_state": "Shavkat Mirziyoyev", "head_of_government": "Abdulla Aripov", "state_age": 66, "gov_age": 62, "state_trait": "industrialist", "gov_trait": "technocrat" },
  "TKM": { "head_of_state": "Serdar Berdimuhamedov", "state_age": 42, "state_trait": "iron_hand" },
  "KGZ": { "head_of_state": "Sadyr Japarov", "head_of_government": "Akylbek Japarov", "state_age": 55, "gov_age": 59, "state_trait": "populist", "gov_trait": "technocrat" },
  "TJK": { "head_of_state": "Emomali Rahmon", "head_of_government": "Kokhir Rasulzoda", "state_age": 71, "gov_age": 62, "state_trait": "iron_hand", "gov_trait": "technocrat" },
  "MNG": { "head_of_state": "Ukhnaagiin Khurelsukh", "head_of_government": "Luvsannamsrain Oyun-Erdene", "state_age": 55, "gov_age": 43, "state_trait": "industrialist", "gov_trait": "reformer" },
  "AFG": { "head_of_state": "Hibatullah Akhundzada", "head_of_government": "Hasan Akhund", "state_age": 62, "gov_age": 78, "state_trait": "ideologue", "gov_trait": "iron_hand" },

  // --- East & Southeast Asia ---
  "CHN": {
    "head_of_state": "Xi Jinping",
    "head_of_government": "Li Qiang",
    "state_age": 70,
    "gov_age": 64,
    "state_trait": "iron_hand",
    "gov_trait": "industrialist",
    "ministers": {
      "foreign": { "name": "Wang Yi", "age": 70, "trait": "diplomat", "option": "realpolitik_envoy" },
      "defense": { "name": "Dong Jun", "age": 63, "trait": "veteran_commander", "option": "deep_battle_marshal" },
      "economy": { "name": "He Lifeng", "age": 68, "trait": "industrialist", "option": "industrial_mobiliser" },
      "interior": { "name": "Wang Xiaohong", "age": 66, "trait": "iron_hand", "option": "security_chief" }
    }
  },
  "TWN": { "head_of_state": "Tsai Ing-wen", "head_of_government": "Chen Chien-jen", "state_age": 67, "gov_age": 72, "state_trait": "reformer", "gov_trait": "technocrat" },
  "JPN": {
    "head_of_state": "Naruhito",
    "head_of_government": "Fumio Kishida",
    "state_age": 63,
    "gov_age": 66,
    "state_trait": "diplomat",
    "gov_trait": "industrialist",
    "ministers": {
      "foreign": { "name": "Yoko Kamikawa", "age": 70, "trait": "diplomat", "option": "coalition_builder" },
      "defense": { "name": "Minoru Kihara", "age": 54, "trait": "hawk", "option": "quartermaster_general" },
      "economy": { "name": "Shunichi Suzuki", "age": 70, "trait": "technocrat", "option": "orthodox_treasurer" },
      "interior": { "name": "Yoshifumi Matsumura", "age": 59, "trait": "reformer", "option": "constitutional_jurist" }
    }
  },
  "KOR": {
    "head_of_state": "Yoon Suk Yeol",
    "head_of_government": "Han Duck-soo",
    "state_age": 63,
    "gov_age": 74,
    "state_trait": "hawk",
    "gov_trait": "technocrat",
    "ministers": {
      "foreign": { "name": "Cho Tae-yul", "age": 68, "trait": "diplomat", "option": "coalition_builder" },
      "defense": { "name": "Shin Won-sik", "age": 65, "trait": "veteran_commander", "option": "offensive_strategist" },
      "economy": { "name": "Choi Sang-mok", "age": 60, "trait": "technocrat", "option": "orthodox_treasurer" },
      "interior": { "name": "Lee Sang-min", "age": 58, "trait": "iron_hand", "option": "security_chief" }
    }
  },
  "PRK": { "head_of_state": "Kim Jong Un", "head_of_government": "Kim Tok Hun", "state_age": 40, "gov_age": 62, "state_trait": "iron_hand", "gov_trait": "industrialist" },
  "VNM": { "head_of_state": "Nguyen Phu Trong", "head_of_government": "Pham Minh Chinh", "state_age": 79, "gov_age": 65, "state_trait": "ideologue", "gov_trait": "industrialist" },
  "LAO": { "head_of_state": "Thongloun Sisoulith", "head_of_government": "Sonexay Siphandone", "state_age": 78, "gov_age": 57, "state_trait": "ideologue", "gov_trait": "technocrat" },
  "KHM": { "head_of_state": "Norodom Sihamoni", "head_of_government": "Hun Manet", "state_age": 70, "gov_age": 46, "state_trait": "diplomat", "gov_trait": "reformer" },
  "THA": { "head_of_state": "Vajiralongkorn", "head_of_government": "Srettha Thavisin", "state_age": 71, "gov_age": 61, "state_trait": "iron_hand", "gov_trait": "industrialist" },
  "MMR": { "head_of_state": "Min Aung Hlaing", "state_age": 67, "state_trait": "veteran_commander" },
  "MYS": { "head_of_state": "Abdullah of Pahang", "head_of_government": "Anwar Ibrahim", "state_age": 64, "gov_age": 76, "state_trait": "diplomat", "gov_trait": "reformer" },
  "SGP": { "head_of_state": "Tharman Shanmugaratnam", "head_of_government": "Lee Hsien Loong", "state_age": 66, "gov_age": 71, "state_trait": "technocrat", "gov_trait": "technocrat" },
  "IDN": { "head_of_state": "Joko Widodo", "head_of_government": "Ma'ruf Amin", "state_age": 62, "gov_age": 80, "state_trait": "industrialist", "gov_trait": "diplomat" },
  "PHL": { "head_of_state": "Bongbong Marcos", "state_age": 66, "state_trait": "industrialist" },
  "BRN": { "head_of_state": "Hassanal Bolkiah", "state_age": 77, "state_trait": "iron_hand" },
  "TLS": { "head_of_state": "Jose Ramos-Horta", "head_of_government": "Xanana Gusmao", "state_age": 74, "gov_age": 77, "state_trait": "diplomat", "gov_trait": "veteran_commander" },

  // --- South Asia ---
  "IND": {
    "head_of_state": "Droupadi Murmu",
    "head_of_government": "Narendra Modi",
    "state_age": 65,
    "gov_age": 73,
    "state_trait": "welfare_architect",
    "gov_trait": "industrialist",
    "ministers": {
      "foreign": { "name": "S. Jaishankar", "age": 68, "trait": "diplomat", "option": "realpolitik_envoy" },
      "defense": { "name": "Rajnath Singh", "age": 72, "trait": "hawk", "option": "quartermaster_general" },
      "economy": { "name": "Nirmala Sitharaman", "age": 64, "trait": "technocrat", "option": "orthodox_treasurer" },
      "interior": { "name": "Amit Shah", "age": 59, "trait": "iron_hand", "option": "security_chief" }
    }
  },
  "PAK": { "head_of_state": "Arif Alvi", "head_of_government": "Anwaar-ul-Haq Kakar", "state_age": 74, "gov_age": 52, "state_trait": "diplomat", "gov_trait": "technocrat" },
  "BGD": { "head_of_state": "Mohammed Shahabuddin", "head_of_government": "Sheikh Hasina", "state_age": 74, "gov_age": 76, "state_trait": "reformer", "gov_trait": "industrialist" },
  "LKA": { "head_of_state": "Ranil Wickremesinghe", "head_of_government": "Dinesh Gunawardena", "state_age": 74, "gov_age": 74, "state_trait": "technocrat", "gov_trait": "reformer" },
  "NPL": { "head_of_state": "Ram Chandra Poudel", "head_of_government": "Pushpa Kamal Dahal", "state_age": 79, "gov_age": 69, "state_trait": "reformer", "gov_trait": "populist" },
  "BTN": { "head_of_state": "Jigme Khesar Namgyel Wangchuck", "head_of_government": "Tshering Tobgay", "state_age": 43, "gov_age": 58, "state_trait": "diplomat", "gov_trait": "reformer" },
  "MDV": { "head_of_state": "Mohamed Muizzu", "state_age": 45, "state_trait": "technocrat" },

  // --- Sub-Saharan Africa ---
  "NGA": { "head_of_state": "Bola Tinubu", "state_age": 71, "state_trait": "technocrat" },
  "ETH": { "head_of_state": "Sahle-Work Zewde", "head_of_government": "Abiy Ahmed", "state_age": 73, "gov_age": 47, "state_trait": "diplomat", "gov_trait": "hawk" },
  "ZAF": { "head_of_state": "Cyril Ramaphosa", "state_age": 71, "state_trait": "diplomat" },
  "KEN": { "head_of_state": "William Ruto", "state_age": 57, "state_trait": "industrialist" },
  "COD": { "head_of_state": "Felix Tshisekedi", "head_of_government": "Jean-Michel Sama Lukonde", "state_age": 60, "gov_age": 46, "state_trait": "populist", "gov_trait": "technocrat" },
  "TZA": { "head_of_state": "Samia Suluhu Hassan", "head_of_government": "Kassim Majaliwa", "state_age": 63, "gov_age": 63, "state_trait": "reformer", "gov_trait": "technocrat" },
  "UGA": { "head_of_state": "Yoweri Museveni", "head_of_government": "Robinah Nabbanja", "state_age": 79, "gov_age": 54, "state_trait": "veteran_commander", "gov_trait": "populist" },
  "GHA": { "head_of_state": "Nana Akufo-Addo", "state_age": 79, "state_trait": "reformer" },
  "CIV": { "head_of_state": "Alassane Ouattara", "head_of_government": "Robert Beugre Mambe", "state_age": 82, "gov_age": 72, "state_trait": "technocrat", "gov_trait": "industrialist" },
  "CMR": { "head_of_state": "Paul Biya", "head_of_government": "Joseph Ngute", "state_age": 90, "gov_age": 69, "state_trait": "iron_hand", "gov_trait": "technocrat" },
  "AGO": { "head_of_state": "Joao Lourenco", "state_age": 69, "state_trait": "veteran_commander" },
  "MOZ": { "head_of_state": "Filipe Nyusi", "head_of_government": "Adriano Maleiane", "state_age": 64, "gov_age": 74, "state_trait": "technocrat", "gov_trait": "technocrat" },
  "MDG": { "head_of_state": "Andry Rajoelina", "head_of_government": "Christian Ntsay", "state_age": 49, "gov_age": 62, "state_trait": "populist", "gov_trait": "technocrat" },
  "ZMB": { "head_of_state": "Hakainde Hichilema", "state_age": 61, "state_trait": "reformer" },
  "ZWE": { "head_of_state": "Emmerson Mnangagwa", "state_age": 81, "state_trait": "iron_hand" },
  "SEN": { "head_of_state": "Macky Sall", "head_of_government": "Amadou Ba", "state_age": 62, "gov_age": 62, "state_trait": "industrialist", "gov_trait": "technocrat" },
  "MLI": { "head_of_state": "Assimi Goita", "head_of_government": "Choguel Kokalla Maiga", "state_age": 41, "gov_age": 66, "state_trait": "veteran_commander", "gov_trait": "populist" },
  "BFA": { "head_of_state": "Ibrahim Traore", "head_of_government": "Apollinaire Kyelem de Tambela", "state_age": 35, "gov_age": 65, "state_trait": "hawk", "gov_trait": "ideologue" },
  "NER": { "head_of_state": "Abdourahamane Tchiani", "head_of_government": "Ali Lamine Zeine", "state_age": 63, "gov_age": 59, "state_trait": "veteran_commander", "gov_trait": "technocrat" },
  "TCD": { "head_of_state": "Mahamat Deby", "head_of_government": "Succes Masra", "state_age": 40, "gov_age": 40, "state_trait": "veteran_commander", "gov_trait": "technocrat" },
  "GIN": { "head_of_state": "Mamady Doumbouya", "head_of_government": "Bernard Goumou", "state_age": 43, "gov_age": 43, "state_trait": "veteran_commander", "gov_trait": "technocrat" },
  "RWA": { "head_of_state": "Paul Kagame", "head_of_government": "Edouard Ngirente", "state_age": 66, "gov_age": 50, "state_trait": "veteran_commander", "gov_trait": "technocrat" },
  "BDI": { "head_of_state": "Evariste Ndayishimiye", "head_of_government": "Gervais Ndirakobuca", "state_age": 55, "gov_age": 54, "state_trait": "veteran_commander", "gov_trait": "iron_hand" },
  "SOM": { "head_of_state": "Hassan Sheikh Mohamud", "head_of_government": "Hamza Abdi Barre", "state_age": 68, "gov_age": 51, "state_trait": "diplomat", "gov_trait": "reformer" },
  "SOL": { "head_of_state": "Muse Bihi Abdi", "state_age": 75, "state_trait": "veteran_commander" },
  "ERI": { "head_of_state": "Isaias Afwerki", "state_age": 77, "state_trait": "iron_hand" },
  "DJI": { "head_of_state": "Ismael Omar Guelleh", "head_of_government": "Abdoulkader Kamil Mohamed", "state_age": 76, "gov_age": 72, "state_trait": "iron_hand", "gov_trait": "technocrat" },
  "GAB": { "head_of_state": "Brice Oligui Nguema", "head_of_government": "Raymond Ndong Sima", "state_age": 48, "gov_age": 68, "state_trait": "veteran_commander", "gov_trait": "technocrat" },
  "COG": { "head_of_state": "Denis Sassou Nguesso", "head_of_government": "Anatole Collinet Makosso", "state_age": 80, "gov_age": 58, "state_trait": "iron_hand", "gov_trait": "technocrat" },
  "GNQ": { "head_of_state": "Teodoro Obiang Nguema Mbasogo", "head_of_government": "Manuela Roka Botey", "state_age": 81, "gov_age": 51, "state_trait": "iron_hand", "gov_trait": "technocrat" },
  "CAF": { "head_of_state": "Faustin-Archange Touadera", "head_of_government": "Felix Moloua", "state_age": 66, "gov_age": 66, "state_trait": "technocrat", "gov_trait": "technocrat" },
  "NAM": { "head_of_state": "Hage Geingob", "head_of_government": "Saara Kuugongelwa", "state_age": 82, "gov_age": 56, "state_trait": "diplomat", "gov_trait": "technocrat" },
  "BWA": { "head_of_state": "Mokgweetsi Masisi", "state_age": 62, "state_trait": "reformer" },
  "LSO": { "head_of_state": "Letsie III", "head_of_government": "Sam Matekane", "state_age": 60, "gov_age": 65, "state_trait": "diplomat", "gov_trait": "industrialist" },
  "SWZ": { "head_of_state": "Mswati III", "head_of_government": "Russell Dlamini", "state_age": 55, "gov_age": 50, "state_trait": "iron_hand", "gov_trait": "technocrat" },
  "MWI": { "head_of_state": "Lazarus Chakwera", "state_age": 68, "state_trait": "reformer" },
  "MUS": { "head_of_state": "Prithvirajsing Roopun", "head_of_government": "Pravind Jugnauth", "state_age": 64, "gov_age": 62, "state_trait": "diplomat", "gov_trait": "technocrat" },
  "SYC": { "head_of_state": "Wavel Ramkalawan", "state_age": 62, "state_trait": "reformer" },
  "COM": { "head_of_state": "Azali Assoumani", "state_age": 65, "state_trait": "iron_hand" },
  "STP": { "head_of_state": "Carlos Vila Nova", "head_of_government": "Patrice Trovoada", "state_age": 64, "gov_age": 61, "state_trait": "reformer", "gov_trait": "technocrat" },
  "CPV": { "head_of_state": "Jose Maria Neves", "head_of_government": "Ulisses Correia e Silva", "state_age": 63, "gov_age": 61, "state_trait": "diplomat", "gov_trait": "technocrat" },
  "GMB": { "head_of_state": "Adama Barrow", "state_age": 58, "state_trait": "reformer" },
  "GNB": { "head_of_state": "Umaro Sissoco Embalo", "head_of_government": "Rui Duarte de Barros", "state_age": 51, "gov_age": 63, "state_trait": "veteran_commander", "gov_trait": "technocrat" },
  "SLE": { "head_of_state": "Julius Maada Bio", "state_age": 59, "state_trait": "veteran_commander" },
  "LBR": { "head_of_state": "George Weah", "state_age": 57, "state_trait": "populist" },
  "TGO": { "head_of_state": "Faure Gnassingbe", "head_of_government": "Victoire Tomegah Dogbe", "state_age": 57, "gov_age": 64, "state_trait": "iron_hand", "gov_trait": "technocrat" },
  "BEN": { "head_of_state": "Patrice Talon", "state_age": 65, "state_trait": "industrialist" },

  // --- Oceania ---
  "AUS": { "head_of_state": "Charles III", "head_of_government": "Anthony Albanese", "state_age": 75, "gov_age": 60, "state_trait": "diplomat", "gov_trait": "welfare_architect" },
  "NZL": { "head_of_state": "Charles III", "head_of_government": "Christopher Luxon", "state_age": 75, "gov_age": 53, "state_trait": "diplomat", "gov_trait": "technocrat" },
  "PNG": { "head_of_state": "Charles III", "head_of_government": "James Marape", "state_age": 75, "gov_age": 52, "state_trait": "diplomat", "gov_trait": "populist" },
  "FJI": { "head_of_state": "Wiliame Katonivere", "head_of_government": "Sitiveni Rabuka", "state_age": 59, "gov_age": 75, "state_trait": "diplomat", "gov_trait": "veteran_commander" },
  "SLB": { "head_of_state": "Charles III", "head_of_government": "Manasseh Sogavare", "state_age": 75, "gov_age": 68, "state_trait": "diplomat", "gov_trait": "populist" },
  "VUT": { "head_of_state": "Nikenike Vurobaravu", "head_of_government": "Charlot Salwai", "state_age": 72, "gov_age": 60, "state_trait": "diplomat", "gov_trait": "reformer" },
  "WSM": { "head_of_state": "Tuimaleali'ifano Va'aleto'a Sualauvi II", "head_of_government": "Fiame Naomi Mata'afa", "state_age": 76, "gov_age": 66, "state_trait": "diplomat", "gov_trait": "reformer" },
  "TON": { "head_of_state": "Tupou VI", "head_of_government": "Siaosi Sovaleni", "state_age": 64, "gov_age": 53, "state_trait": "diplomat", "gov_trait": "technocrat" },
  "FSM": { "head_of_state": "Wesley Simina", "state_age": 62, "state_trait": "diplomat" },
  "MHL": { "head_of_state": "David Kabua", "state_age": 72, "state_trait": "diplomat" },
  "PLW": { "head_of_state": "Surangel Whipps Jr.", "state_age": 55, "state_trait": "technocrat" },
  "KIR": { "head_of_state": "Taneti Maamau", "state_age": 63, "state_trait": "reformer" },
  "TUV": { "head_of_state": "Charles III", "head_of_government": "Kausea Natano", "state_age": 75, "gov_age": 66, "state_trait": "diplomat", "gov_trait": "reformer" },
  "NRU": { "head_of_state": "David Adeang", "state_age": 54, "state_trait": "technocrat" },

  // Territories & Autonomous Entities
  "HKG": { "head_of_state": "John Lee", "state_age": 66, "state_trait": "iron_hand" },
  "MAC": { "head_of_state": "Ho Iat Seng", "state_age": 66, "state_trait": "technocrat" },
  "GRL": { "head_of_state": "Mute Egede", "state_age": 36, "state_trait": "reformer" },
  "FRO": { "head_of_state": "Aksel V. Johannesen", "state_age": 51, "state_trait": "reformer" },
  "GIB": { "head_of_state": "Fabian Picardo", "state_age": 51, "state_trait": "technocrat" },
  "BMU": { "head_of_state": "Edward David Burt", "state_age": 44, "state_trait": "technocrat" },
  "CYM": { "head_of_state": "Julianna O'Connor-Connolly", "state_age": 62, "state_trait": "reformer" },
  "ABW": { "head_of_state": "Evelyn Wever-Croes", "state_age": 57, "state_trait": "technocrat" },
  "CUW": { "head_of_state": "Gilmar Pisas", "state_age": 52, "state_trait": "populist" },
  "SXM": { "head_of_state": "Silveria Jacobs", "state_age": 55, "state_trait": "reformer" },
  "NCL": { "head_of_state": "Louis Mapou", "state_age": 65, "state_trait": "populist" },
  "PYF": { "head_of_state": "Moetai Brotherson", "state_age": 54, "state_trait": "reformer" },
  "GUM": { "head_of_state": "Lou Leon Guerrero", "state_age": 70, "state_trait": "technocrat" },
  "CYN": { "head_of_state": "Ersin Tatar", "head_of_government": "Unal Ustel", "state_age": 63, "gov_age": 69, "state_trait": "hawk", "gov_trait": "technocrat" },
  "ESH": { "head_of_state": "Brahim Ghali", "head_of_government": "Bouchraya Hammoudi Bayoun", "state_age": 74, "gov_age": 69, "state_trait": "veteran_commander", "gov_trait": "diplomat" }
};

const header = `// Deal or Force - Heads of state, heads of government and cabinet ministers on 1 January 2024 (Scripts/Simulation/Rulers.cs).
//
// Every sovereign state, autonomous realm, and de-facto faction has its real-life leadership
// and exact historical ages as of January 2024. Dynamic or released countries draw deterministic
// leadership from their demography and government forms.
`;

const formatted = header + JSON.stringify(data, null, 2) + '\n';
fs.writeFileSync('game/history/countries/2024/25_rulers.json', formatted, 'utf8');
console.log('Successfully written ' + Object.keys(data).length + ' countries to 25_rulers.json');
